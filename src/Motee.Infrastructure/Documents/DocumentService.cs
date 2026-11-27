using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Documents;
using Motee.Domain.Documents;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Documents;

internal sealed class DocumentService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IDocumentService
{
    public async Task<IReadOnlyList<FolderDto>> FoldersAsync(
        CancellationToken cancellationToken = default)
    {
        List<DocumentFolder> folders = await dbContext.DocumentFolders
            .AsNoTracking()
            .OrderBy(folder => folder.Name)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, int> counts = await dbContext.HrDocuments
            .AsNoTracking()
            .Where(document => document.TrashedAt == null)
            .GroupBy(document => document.FolderId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);

        return [.. folders.Select(folder => Describe(folder, counts.GetValueOrDefault(folder.Id)))];
    }

    public async Task<FolderResult> CreateFolderAsync(
        FolderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return FolderResult.Failed(DocumentOutcome.NameRequired);
        }

        string name = request.Name.Trim();

        // Scoped to the parent, not the tenant. "Policies" under Engineering and
        // "Policies" under Sales are ordinary filing; two at the same level are not.
        if (await dbContext.DocumentFolders.AnyAsync(
                folder => folder.ParentId == request.ParentId && folder.Name == name,
                cancellationToken))
        {
            return FolderResult.Failed(DocumentOutcome.DuplicateFolderName);
        }

        if (request.ParentId is Guid parentId
            && !await dbContext.DocumentFolders.AnyAsync(
                folder => folder.Id == parentId, cancellationToken))
        {
            return FolderResult.Failed(DocumentOutcome.FolderNotFound);
        }

        DocumentFolder created = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = FolderType.Custom,
            ParentId = request.ParentId,
            CreatedByUserId = UserId(),
            CreatedAt = timeProvider.GetUtcNow(),
        };

        dbContext.DocumentFolders.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return FolderResult.Ok(Describe(created, documentCount: 0));
    }

    public async Task<FolderResult> RenameFolderAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return FolderResult.Failed(DocumentOutcome.NameRequired);
        }

        DocumentFolder? folder = await dbContext.DocumentFolders
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (folder is null)
        {
            return FolderResult.Failed(DocumentOutcome.FolderNotFound);
        }

        string trimmed = name.Trim();

        if (await dbContext.DocumentFolders.AnyAsync(
                candidate => candidate.ParentId == folder.ParentId
                    && candidate.Name == trimmed
                    && candidate.Id != id,
                cancellationToken))
        {
            return FolderResult.Failed(DocumentOutcome.DuplicateFolderName);
        }

        // Renaming a system folder is allowed — a company calling them something
        // else is their business. Deleting one is not, because screens reach them
        // by purpose rather than by name.
        folder.Name = trimmed;

        await dbContext.SaveChangesAsync(cancellationToken);

        return FolderResult.Ok(Describe(folder, await CountAsync(id, cancellationToken)));
    }

    public async Task<DocumentOutcome> DeleteFolderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        DocumentFolder? folder = await dbContext.DocumentFolders
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (folder is null)
        {
            return DocumentOutcome.FolderNotFound;
        }

        if (!Domain.Documents.Documents.IsDeletable(folder))
        {
            return DocumentOutcome.FolderNotDeletable;
        }

        // Emptying it first is the point: deleting a folder of documents by
        // accident is not recoverable the way trashing one document is. Trashed
        // ones do not count — they are already out of the way.
        if (await dbContext.HrDocuments.AnyAsync(
                document => document.FolderId == id && document.TrashedAt == null,
                cancellationToken))
        {
            return DocumentOutcome.FolderNotEmpty;
        }

        if (await dbContext.DocumentFolders.AnyAsync(
                child => child.ParentId == id, cancellationToken))
        {
            return DocumentOutcome.FolderNotEmpty;
        }

        dbContext.DocumentFolders.Remove(folder);
        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentOutcome.Succeeded;
    }

    public async Task<PagedResult<DocumentDto>> ListAsync(
        DocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<HrDocument> source = dbContext.HrDocuments.AsNoTracking();

        // Self-service uploads awaiting review are not part of the library yet.
        //
        // This filter is what makes the review queue mean anything. Without it an
        // employee could put whatever they liked on the company's filing by uploading
        // it, and HR would find it already there before anybody had looked. Rejected
        // ones stay out too — they are still visible to whoever uploaded them, which is
        // how they can tell a refusal from an upload that never arrived.
        source = source.Where(document =>
            document.ReviewStatus == null
            || document.ReviewStatus == DocumentReviewStatus.Accepted);

        if (!query.IncludeTrashed)
        {
            source = source.Where(document => document.TrashedAt == null);
        }

        if (!query.IncludeArchived)
        {
            source = source.Where(document => !document.IsArchived);
        }

        if (query.FolderId is Guid folderId)
        {
            source = source.Where(document => document.FolderId == folderId);
        }

        if (query.Category is DocumentCategory category)
        {
            source = source.Where(document => document.Category == category);
        }

        if (query.ExpiredOnly == true)
        {
            DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

            source = source.Where(document =>
                document.ExpiryDate != null && document.ExpiryDate < today);
        }

        int total = await source.CountAsync(cancellationToken);

        List<HrDocument> page = await source
            .OrderByDescending(document => document.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<DocumentDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<DocumentDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        HrDocument? document = await dbContext.HrDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return document is null ? null : (await DescribeAsync([document], cancellationToken))[0];
    }

    public async Task<DocumentResult> CreateAsync(
        CreateDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return DocumentResult.Failed(DocumentOutcome.NameRequired);
        }

        if (!await dbContext.DocumentFolders.AnyAsync(
                folder => folder.Id == request.FolderId, cancellationToken))
        {
            return DocumentResult.Failed(DocumentOutcome.FolderNotFound);
        }

        // The tenant filter makes this a tenant check too: a file uploaded by
        // another company simply is not visible here.
        long? size = await SizeOfAsync(request.StoredFileId, cancellationToken);

        if (size is null)
        {
            return DocumentResult.Failed(DocumentOutcome.FileNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        HrDocument document = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Category = request.Category,
            FolderId = request.FolderId,
            Description = request.Description?.Trim(),
            ExpiryDate = request.ExpiryDate,
            Assignment = request.Assignment,
            AssignedDepartmentIds = [.. request.AssignedDepartmentIds ?? []],
            RequiresAcknowledgement = request.RequiresAcknowledgement,
            CreatedByUserId = UserId(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.HrDocuments.Add(document);

        dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            Version = 1,
            StoredFileId = request.StoredFileId,
            SizeBytes = size.Value,
            UploadedByUserId = UserId(),
            UploadedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    public async Task<DocumentResult> UpdateAsync(
        Guid id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        HrDocument? document = await dbContext.HrDocuments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return DocumentResult.Failed(DocumentOutcome.NameRequired);
        }

        if (!await dbContext.DocumentFolders.AnyAsync(
                folder => folder.Id == request.FolderId, cancellationToken))
        {
            return DocumentResult.Failed(DocumentOutcome.FolderNotFound);
        }

        document.Name = request.Name.Trim();
        document.Category = request.Category;
        document.FolderId = request.FolderId;
        document.Description = request.Description?.Trim();
        document.ExpiryDate = request.ExpiryDate;
        document.Assignment = request.Assignment;
        document.AssignedDepartmentIds = [.. request.AssignedDepartmentIds ?? []];
        document.RequiresAcknowledgement = request.RequiresAcknowledgement;
        document.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    public async Task<DocumentResult> AddVersionAsync(
        Guid id,
        AddVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        HrDocument? document = await dbContext.HrDocuments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotFound);
        }

        long? size = await SizeOfAsync(request.StoredFileId, cancellationToken);

        if (size is null)
        {
            return DocumentResult.Failed(DocumentOutcome.FileNotFound);
        }

        int highest = await dbContext.DocumentVersions
            .Where(version => version.DocumentId == id)
            .MaxAsync(version => (int?)version.Version, cancellationToken) ?? 0;

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Added, never replacing. Acknowledgements point at the version they were
        // given, so the old one has to stay for them to keep meaning anything.
        dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            DocumentId = id,
            Version = highest + 1,
            StoredFileId = request.StoredFileId,
            SizeBytes = size.Value,
            Notes = request.Notes?.Trim(),
            UploadedByUserId = UserId(),
            UploadedAt = now,
        });

        document.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    public Task<DocumentResult> ArchiveAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        SetAsync(id, document => document.IsArchived = true, cancellationToken);

    public Task<DocumentResult> TrashAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        SetAsync(
            id,
            document => document.TrashedAt = timeProvider.GetUtcNow(),
            cancellationToken);

    public Task<DocumentResult> RestoreAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        SetAsync(
            id,
            document =>
            {
                document.TrashedAt = null;
                document.IsArchived = false;
            },
            cancellationToken);

    public async Task<DocumentResult> ShareAsync(
        Guid id,
        ShareDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        HrDocument? document = await dbContext.HrDocuments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotFound);
        }

        if (!await dbContext.Employees.AnyAsync(
                employee => employee.Id == request.EmployeeId, cancellationToken))
        {
            return DocumentResult.Failed(DocumentOutcome.EmployeeNotFound);
        }

        // Sharing twice is a permission change dressed as a share, and the two
        // rows would then disagree about what they may do with it.
        if (await dbContext.DocumentShares.AnyAsync(
                share => share.DocumentId == id && share.EmployeeId == request.EmployeeId,
                cancellationToken))
        {
            return DocumentResult.Failed(DocumentOutcome.AlreadyShared);
        }

        dbContext.DocumentShares.Add(new DocumentShare
        {
            Id = Guid.NewGuid(),
            DocumentId = id,
            EmployeeId = request.EmployeeId,
            Permission = request.Permission,
            SharedByUserId = UserId(),
            SharedAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    public async Task<DocumentOutcome> UnshareAsync(
        Guid id,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        DocumentShare? share = await dbContext.DocumentShares
            .FirstOrDefaultAsync(
                candidate => candidate.DocumentId == id && candidate.EmployeeId == employeeId,
                cancellationToken);

        if (share is null)
        {
            return DocumentOutcome.NotFound;
        }

        dbContext.DocumentShares.Remove(share);
        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentOutcome.Succeeded;
    }

    public async Task<IReadOnlyList<DocumentDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        Guid? departmentId = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.DepartmentId)
            .FirstOrDefaultAsync(cancellationToken);

        IQueryable<HrDocument> live = dbContext.HrDocuments
            .AsNoTracking()
            .Where(document => document.TrashedAt == null && !document.IsArchived);

        // Global and shared translate to SQL. Department assignment does not:
        // AssignedDepartmentIds is jsonb behind a value converter, so Contains over
        // it is opaque to the translator and throws rather than falling back.
        List<HrDocument> reachable = await live
            .Where(document => document.Assignment == AssignmentScope.Global
                || dbContext.DocumentShares.Any(share =>
                    share.DocumentId == document.Id && share.EmployeeId == employeeId))
            .ToListAsync(cancellationToken);

        if (departmentId is Guid department)
        {
            // Narrowed in SQL to the ones assigned by department — a handful — then
            // matched in memory. Loading every document to filter would be the
            // alternative, and that grows with the whole filing cabinet.
            List<HrDocument> byDepartment = await live
                .Where(document => document.Assignment == AssignmentScope.Department)
                .ToListAsync(cancellationToken);

            reachable.AddRange(byDepartment.Where(document =>
                document.AssignedDepartmentIds.Contains(department)));
        }

        // Their own uploads, whatever the review has decided. Included at every status
        // on purpose: somebody who uploaded a certificate has to be able to see that it
        // is still waiting, and to read why it was refused. Hiding a rejection would
        // leave them unable to tell it from an upload that never arrived.
        //
        // Not reached by the three routes above — an upload awaiting review is assigned
        // to nobody and shared with nobody, which is exactly what keeps it out of the
        // company's library until HR accepts it.
        reachable.AddRange(await live
            .Where(document => document.UploadedByEmployeeId == employeeId)
            .ToListAsync(cancellationToken));

        // One list, however it arrived. Which of the three routes reached them is
        // not the employee's problem — and a document both shared and global would
        // otherwise appear twice.
        return await DescribeAsync(
            [
                .. reachable
                    .DistinctBy(document => document.Id)
                    .OrderByDescending(document => document.CreatedAt),
            ],
            cancellationToken);
    }

    public async Task<DocumentResult> AcknowledgeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return DocumentResult.Failed(DocumentOutcome.EmployeeNotFound);
        }

        HrDocument? document = await dbContext.HrDocuments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotFound);
        }

        if (!document.RequiresAcknowledgement)
        {
            return DocumentResult.Failed(DocumentOutcome.AcknowledgementNotRequired);
        }

        DocumentVersion? current = await CurrentVersionAsync(id, cancellationToken);

        if (current is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotFound);
        }

        // Against the version, not the document. Twice on the same one would move
        // the date and make a late acknowledgement look prompt.
        if (await dbContext.DocumentAcknowledgements.AnyAsync(
                ack => ack.VersionId == current.Id && ack.EmployeeId == employeeId,
                cancellationToken))
        {
            return DocumentResult.Failed(DocumentOutcome.AlreadyAcknowledged);
        }

        dbContext.DocumentAcknowledgements.Add(new DocumentAcknowledgement
        {
            Id = Guid.NewGuid(),
            DocumentId = id,
            VersionId = current.Id,
            EmployeeId = employeeId,
            AcknowledgedAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    public async Task<AcknowledgementReportDto?> AcknowledgementsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        HrDocument? document = await dbContext.HrDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return null;
        }

        DocumentVersion? current = await CurrentVersionAsync(id, cancellationToken);

        if (current is null)
        {
            return null;
        }

        List<Guid> assigned = await AssignedToAsync(document, cancellationToken);

        Dictionary<Guid, string> names = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => assigned.Contains(employee.Id))
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);

        // Against the current version only. Somebody who agreed to version 1 has
        // not agreed to version 2, and a report that counted them would be the
        // evidence a company relied on for a policy nobody has read.
        List<DocumentAcknowledgement> acknowledged = await dbContext.DocumentAcknowledgements
            .AsNoTracking()
            .Where(ack => ack.VersionId == current.Id && assigned.Contains(ack.EmployeeId))
            .ToListAsync(cancellationToken);

        HashSet<Guid> done = [.. acknowledged.Select(ack => ack.EmployeeId)];

        return new AcknowledgementReportDto
        {
            DocumentId = id,
            CurrentVersion = current.Version,
            TotalAssigned = assigned.Count,
            Acknowledged =
            [
                .. acknowledged.Select(ack => new AcknowledgedByDto
                {
                    EmployeeId = ack.EmployeeId,
                    EmployeeName = names.GetValueOrDefault(ack.EmployeeId) ?? "Unknown",
                    AcknowledgedAt = ack.AcknowledgedAt,
                }),
            ],

            // Named, not counted. "Fourteen outstanding" is a number; a list is
            // something somebody can chase.
            Outstanding =
            [
                .. assigned
                    .Where(employeeId => !done.Contains(employeeId))
                    .Select(employeeId => names.GetValueOrDefault(employeeId) ?? "Unknown")
                    .Order(StringComparer.Ordinal),
            ],
        };
    }

    // Everybody the document reaches, worked out from its scope.
    // ── self-service ────────────────────────────────────────────────────────

    public async Task<DocumentResult> UploadMineAsync(
        SelfUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return DocumentResult.Failed(DocumentOutcome.NoEmployeeRecord);
        }

        string? name = Trimmed(request.Name);

        if (name is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NameRequired);
        }

        long? size = await SizeOfAsync(request.FileId, cancellationToken);

        if (size is null)
        {
            return DocumentResult.Failed(DocumentOutcome.FileNotFound);
        }

        // Their own drawer, or one created now. FolderId is not nullable on the row —
        // every document is filed somewhere — so "no folder" means the drawer that is
        // theirs by default rather than a null nobody can list.
        Guid folderId;

        if (request.FolderId is Guid chosen)
        {
            DocumentFolder? folder = await MyFolderAsync(me, chosen, cancellationToken);

            if (folder is null)
            {
                return DocumentResult.Failed(DocumentOutcome.FolderNotFound);
            }

            folderId = folder.Id;
        }
        else
        {
            folderId = (await DrawerAsync(me, cancellationToken)).Id;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        HrDocument document = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = request.Category,
            FolderId = folderId,
            Description = Trimmed(request.Description),
            ExpiryDate = request.ExpiryDate,

            // Theirs alone. Not Global or Department: an upload awaiting review is one
            // person's document, and assigning it to anybody else before HR has looked
            // at it would publish whatever they chose to upload.
            Assignment = AssignmentScope.Specific,
            UploadedByEmployeeId = me,

            // Awaiting review, which is what keeps it out of the library. A document
            // waiting to be accepted is not part of the record — otherwise an employee
            // could put anything on their own file by uploading it.
            ReviewStatus = DocumentReviewStatus.AwaitingReview,
            CreatedByUserId = UserId(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.HrDocuments.Add(document);

        dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            Version = 1,
            StoredFileId = request.FileId,
            SizeBytes = size.Value,
            UploadedByUserId = UserId(),
            UploadedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    public async Task<IReadOnlyList<FolderDto>> MyFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return [];
        }

        List<DocumentFolder> mine = await dbContext.DocumentFolders
            .AsNoTracking()
            .Where(folder => folder.OwnerEmployeeId == me)
            .OrderBy(folder => folder.Name)
            .ToListAsync(cancellationToken);

        List<FolderDto> described = [];

        foreach (DocumentFolder folder in mine)
        {
            described.Add(Describe(folder, await CountAsync(folder.Id, cancellationToken)));
        }

        return described;
    }

    public async Task<FolderResult> SaveMyFolderAsync(
        Guid? id,
        MyFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return FolderResult.Failed(DocumentOutcome.NoEmployeeRecord);
        }

        string? name = Trimmed(request.Name);

        if (name is null)
        {
            return FolderResult.Failed(DocumentOutcome.NameRequired);
        }

        // Within their own drawers only. Two of the company's folders may share a name
        // with two of theirs without either being ambiguous, because nobody ever sees
        // both lists at once.
        bool taken = await dbContext.DocumentFolders.AnyAsync(
            folder => folder.OwnerEmployeeId == me
                && folder.Name == name
                && (id == null || folder.Id != id),
            cancellationToken);

        if (taken)
        {
            return FolderResult.Failed(DocumentOutcome.DuplicateFolderName);
        }

        DocumentFolder folder;

        if (id is Guid existing)
        {
            DocumentFolder? found = await MyFolderAsync(me, existing, cancellationToken, track: true);

            if (found is null)
            {
                return FolderResult.Failed(DocumentOutcome.NotYours);
            }

            folder = found;
        }
        else
        {
            folder = new DocumentFolder
            {
                Id = Guid.NewGuid(),
                Type = FolderType.Personal,
                OwnerEmployeeId = me,

                // Inside their drawer, not beside it. The drawer is the root of their
                // tree and is identified by having no parent — so a folder they create
                // has to hang off it, or there would be two roots and nothing could
                // tell which one "no folder chosen" meant.
                ParentId = (await DrawerAsync(me, cancellationToken)).Id,
                Name = name,
                CreatedByUserId = UserId(),
                CreatedAt = timeProvider.GetUtcNow(),
            };

            dbContext.DocumentFolders.Add(folder);
        }

        folder.Name = name;
        folder.Colour = Trimmed(request.Colour);

        await dbContext.SaveChangesAsync(cancellationToken);

        return FolderResult.Ok(
            Describe(folder, await CountAsync(folder.Id, cancellationToken)));
    }

    public async Task<DocumentOutcome> DeleteMyFolderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return DocumentOutcome.NoEmployeeRecord;
        }

        DocumentFolder? folder = await MyFolderAsync(me, id, cancellationToken, track: true);

        if (folder is null)
        {
            return DocumentOutcome.NotYours;
        }

        // Emptied first, like a company folder. Deleting a drawer of documents by
        // accident is not recoverable the way binning one document is.
        if (await CountAsync(folder.Id, cancellationToken) > 0)
        {
            return DocumentOutcome.FolderNotEmpty;
        }

        dbContext.DocumentFolders.Remove(folder);

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentOutcome.Succeeded;
    }

    public async Task<DocumentResult> MoveMineAsync(
        Guid id,
        Guid? folderId,
        CancellationToken cancellationToken = default)
    {
        (HrDocument? document, DocumentOutcome refusal) = await MineToActOnAsync(
            id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(refusal);
        }

        Guid me = document.UploadedByEmployeeId!.Value;

        Guid target;

        if (folderId is Guid chosen)
        {
            DocumentFolder? folder = await MyFolderAsync(me, chosen, cancellationToken);

            if (folder is null)
            {
                return DocumentResult.Failed(DocumentOutcome.FolderNotFound);
            }

            target = folder.Id;
        }
        else
        {
            target = (await DrawerAsync(me, cancellationToken)).Id;
        }

        document.FolderId = target;
        document.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    public async Task<DocumentResult> TrashMineAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await SetTrashedAsync(id, timeProvider.GetUtcNow(), cancellationToken);

    public async Task<DocumentResult> RestoreMineAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await SetTrashedAsync(id, null, cancellationToken);

    private async Task<DocumentResult> SetTrashedAsync(
        Guid id,
        DateTimeOffset? trashedAt,
        CancellationToken cancellationToken)
    {
        (HrDocument? document, DocumentOutcome refusal) = await MineToActOnAsync(
            id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(refusal);
        }

        document.TrashedAt = trashedAt;
        document.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    // One document the caller may act on, or the reason they may not.
    //
    // The ownership check is the whole of self-service document safety: /documents/mine
    // returns what HR assigned as well as what the employee uploaded, and only the
    // second is theirs to touch.
    private async Task<(HrDocument? Document, DocumentOutcome Refusal)> MineToActOnAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return (null, DocumentOutcome.NoEmployeeRecord);
        }

        HrDocument? document = await dbContext.HrDocuments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return (null, DocumentOutcome.NotFound);
        }

        return Domain.Documents.Documents.IsOwnedBy(document, me)
            ? (document, DocumentOutcome.Succeeded)
            : (null, DocumentOutcome.NotYours);
    }

    private Task<DocumentFolder?> MyFolderAsync(
        Guid employeeId,
        Guid folderId,
        CancellationToken cancellationToken,
        bool track = false)
    {
        IQueryable<DocumentFolder> folders = track
            ? dbContext.DocumentFolders
            : dbContext.DocumentFolders.AsNoTracking();

        return folders.FirstOrDefaultAsync(
            folder => folder.Id == folderId && folder.OwnerEmployeeId == employeeId,
            cancellationToken);
    }

    // Their default drawer, created the first time anything needs one. FolderType
    // already has Personal for exactly this — "somebody's own drawer, visible to them
    // and to whoever administers documents, which is the difference between private and
    // secret" — and until now nothing could reach it.
    //
    // Identified by being the root of their tree: one drawer per person, with any
    // folders they make inside it. Identifying it as "Personal with no parent" alone
    // was wrong, because every folder they create is also Personal — so the first one
    // became the drawer, and moving a document out of it moved it to itself.
    private async Task<DocumentFolder> DrawerAsync(
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        DocumentFolder? existing = await dbContext.DocumentFolders
            .FirstOrDefaultAsync(
                folder => folder.OwnerEmployeeId == employeeId
                    && folder.Type == FolderType.Personal
                    && folder.ParentId == null,
                cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        DocumentFolder drawer = new()
        {
            Id = Guid.NewGuid(),
            Name = "My documents",
            Type = FolderType.Personal,
            OwnerEmployeeId = employeeId,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        dbContext.DocumentFolders.Add(drawer);

        return drawer;
    }

    // ── HR's review queue ───────────────────────────────────────────────────

    public async Task<PagedResult<DocumentDto>> ReviewsAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<HrDocument> waiting = dbContext.HrDocuments
            .AsNoTracking()
            .Where(document => document.ReviewStatus == DocumentReviewStatus.AwaitingReview);

        int total = await waiting.CountAsync(cancellationToken);

        // Oldest first: a queue is worked from the end that has been waiting longest.
        List<HrDocument> page = await waiting
            .OrderBy(document => document.CreatedAt)
            .ThenBy(document => document.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<DocumentDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public Task<DocumentResult> AcceptUploadAsync(
        Guid id,
        string? note = null,
        CancellationToken cancellationToken = default) =>
        DecideAsync(id, DocumentReviewStatus.Accepted, note, cancellationToken);

    public Task<DocumentResult> RejectUploadAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default) =>
        DecideAsync(id, DocumentReviewStatus.Rejected, reason, cancellationToken);

    private async Task<DocumentResult> DecideAsync(
        Guid id,
        DocumentReviewStatus decision,
        string? note,
        CancellationToken cancellationToken)
    {
        string? trimmed = Trimmed(note);

        // A rejection has to say why. "Your document was rejected" and nothing else
        // sends somebody to HR to ask, which is the cost this avoids.
        if (decision == DocumentReviewStatus.Rejected && trimmed is null)
        {
            return DocumentResult.Failed(DocumentOutcome.ReasonRequired);
        }

        HrDocument? document = await dbContext.HrDocuments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotFound);
        }

        // Never a self-service upload. HR filing a document is not a request for
        // permission, so there is nothing here to decide.
        if (document.ReviewStatus is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotAwaitingReview);
        }

        if (!Domain.Documents.Documents.AwaitsReview(document))
        {
            return DocumentResult.Failed(DocumentOutcome.AlreadyReviewed);
        }

        document.ReviewStatus = decision;
        document.ReviewedByUserId = UserId();
        document.ReviewedAt = timeProvider.GetUtcNow();
        document.ReviewNote = trimmed;
        document.UpdatedAt = document.ReviewedAt.Value;

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    private async Task<List<Guid>> AssignedToAsync(
        HrDocument document,
        CancellationToken cancellationToken) =>
        document.Assignment switch
        {
            AssignmentScope.Global => await dbContext.Employees
                .AsNoTracking()
                .Select(employee => employee.Id)
                .ToListAsync(cancellationToken),

            AssignmentScope.Department => await dbContext.Employees
                .AsNoTracking()
                .Where(employee => employee.DepartmentId != null
                    && document.AssignedDepartmentIds.Contains(employee.DepartmentId.Value))
                .Select(employee => employee.Id)
                .ToListAsync(cancellationToken),

            _ => await dbContext.DocumentShares
                .AsNoTracking()
                .Where(share => share.DocumentId == document.Id)
                .Select(share => share.EmployeeId)
                .ToListAsync(cancellationToken),
        };

    private Task<DocumentVersion?> CurrentVersionAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        dbContext.DocumentVersions
            .AsNoTracking()
            .Where(version => version.DocumentId == documentId)
            .OrderByDescending(version => version.Version)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<DocumentResult> SetAsync(
        Guid id,
        Action<HrDocument> apply,
        CancellationToken cancellationToken)
    {
        HrDocument? document = await dbContext.HrDocuments
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (document is null)
        {
            return DocumentResult.Failed(DocumentOutcome.NotFound);
        }

        apply(document);
        document.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return DocumentResult.Ok((await DescribeAsync([document], cancellationToken))[0]);
    }

    private Task<long?> SizeOfAsync(Guid storedFileId, CancellationToken cancellationToken) =>
        dbContext.StoredFiles
            .AsNoTracking()
            .Where(file => file.Id == storedFileId)
            .Select(file => (long?)file.SizeBytes)
            .FirstOrDefaultAsync(cancellationToken);

    private Task<int> CountAsync(Guid folderId, CancellationToken cancellationToken) =>
        dbContext.HrDocuments.CountAsync(
            document => document.FolderId == folderId && document.TrashedAt == null,
            cancellationToken);

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    // Null for whitespace, so an empty box and an absent field mean the same thing —
    // and a required name cannot be satisfied with a space.
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static FolderDto Describe(DocumentFolder folder, int documentCount) => new()
    {
        Id = folder.Id,
        Name = folder.Name,
        Type = folder.Type,
        ParentId = folder.ParentId,
        DocumentCount = documentCount,
        CanDelete = Domain.Documents.Documents.IsDeletable(folder),
        Colour = folder.Colour,
    };

    private async Task<IReadOnlyList<DocumentDto>> DescribeAsync(
        IReadOnlyList<HrDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return [];
        }

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        List<Guid> ids = [.. documents.Select(document => document.Id)];

        // Batched: versions, shares and folder names for the whole page at once
        // rather than per row.
        List<DocumentVersion> versions = await dbContext.DocumentVersions
            .AsNoTracking()
            .Where(version => ids.Contains(version.DocumentId))
            .ToListAsync(cancellationToken);

        List<DocumentShare> shares = await dbContext.DocumentShares
            .AsNoTracking()
            .Where(share => ids.Contains(share.DocumentId))
            .ToListAsync(cancellationToken);

        List<Guid> people =
        [
            .. shares.Select(share => share.EmployeeId).Distinct(),
        ];

        Dictionary<Guid, string> names = people.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => people.Contains(employee.Id))
                .ToDictionaryAsync(
                    employee => employee.Id,
                    employee => employee.FirstName + " " + employee.LastName,
                    cancellationToken);

        // Who uploaded the self-service ones. Read from the employee rather than stored
        // on the row, so a name change follows the person.
        List<Guid> uploaderIds =
        [
            .. documents.Where(document => document.UploadedByEmployeeId is not null)
                .Select(document => document.UploadedByEmployeeId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> uploaders = uploaderIds.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => uploaderIds.Contains(employee.Id))
                .ToDictionaryAsync(
                    employee => employee.Id,
                    employee => employee.FirstName + " " + employee.LastName,
                    cancellationToken);

        List<Guid> folderIds = [.. documents.Select(document => document.FolderId).Distinct()];

        Dictionary<Guid, string> folders = await dbContext.DocumentFolders
            .AsNoTracking()
            .Where(folder => folderIds.Contains(folder.Id))
            .ToDictionaryAsync(folder => folder.Id, folder => folder.Name, cancellationToken);

        List<Guid> currentVersionIds =
        [
            .. versions
                .GroupBy(version => version.DocumentId)
                .Select(group => group.OrderByDescending(version => version.Version).First().Id),
        ];

        HashSet<Guid> acknowledgedByMe = me is Guid employeeId
            ?
            [
                .. await dbContext.DocumentAcknowledgements
                    .AsNoTracking()
                    .Where(ack => ack.EmployeeId == employeeId
                        && currentVersionIds.Contains(ack.VersionId))
                    .Select(ack => ack.DocumentId)
                    .ToListAsync(cancellationToken),
            ]
            : [];

        return
        [
            .. documents.Select(document => new DocumentDto
            {
                Id = document.Id,
                Name = document.Name,
                Category = document.Category,
                FolderId = document.FolderId,
                FolderName = folders.GetValueOrDefault(document.FolderId),
                Description = document.Description,
                ExpiryDate = document.ExpiryDate,
                IsExpired = Domain.Documents.Documents.IsExpired(document, today),
                Assignment = document.Assignment,
                AssignedDepartmentIds = document.AssignedDepartmentIds,
                RequiresAcknowledgement = document.RequiresAcknowledgement,
                IsArchived = document.IsArchived,
                IsTrashed = Domain.Documents.Documents.IsTrashed(document),
                Versions =
                [
                    .. versions
                        .Where(version => version.DocumentId == document.Id)
                        .OrderByDescending(version => version.Version)
                        .Select(version => new DocumentVersionDto
                        {
                            Id = version.Id,
                            Version = version.Version,
                            StoredFileId = version.StoredFileId,
                            SizeBytes = version.SizeBytes,
                            Notes = version.Notes,
                            UploadedByName = null,
                            UploadedAt = version.UploadedAt,
                        }),
                ],
                Shares =
                [
                    .. shares
                        .Where(share => share.DocumentId == document.Id)
                        .Select(share => new DocumentShareDto
                        {
                            EmployeeId = share.EmployeeId,
                            EmployeeName = names.GetValueOrDefault(share.EmployeeId) ?? "Unknown",
                            Permission = share.Permission,
                            SharedAt = share.SharedAt,
                        }),
                ],

                // Null when the document asks for none, which is a different thing
                // from "not yet" and reads differently on screen.
                AcknowledgedByMe = document.RequiresAcknowledgement
                    ? acknowledgedByMe.Contains(document.Id)
                    : null,
                UploadedByEmployeeId = document.UploadedByEmployeeId,
                UploadedByName = document.UploadedByEmployeeId is Guid uploader
                    ? uploaders.GetValueOrDefault(uploader)
                    : null,
                ReviewStatus = document.ReviewStatus,
                ReviewNote = document.ReviewNote,

                // Theirs to move, bin or re-file. Only their own uploads: the
                // self-service list also carries what HR assigned, and binning the
                // staff handbook off your own file is not yours to do.
                IsMine = me is Guid viewer
                    && Domain.Documents.Documents.IsOwnedBy(document, viewer),
                CreatedAt = document.CreatedAt,
            }),
        ];
    }
}
