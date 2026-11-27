using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Application.Exports;
using Motee.Application.Reports;

namespace Motee.Api.Controllers;

// Reporting over the declared datasets.
//
// No [RequiresPermission] on either endpoint, deliberately, and this is the one place
// in the API where that is the safer choice rather than the laxer one.
//
// A reports module permission would resolve a data scope for *reports*, which says
// nothing about how much of the employee list or the leave register the caller may
// read — and the service needs the scope for the dataset's own module to narrow the
// query correctly. Gating here would put the wrong scope in HttpContext.Items and
// invite somebody to reach for it.
//
// So the check is per dataset, inside the service, against that dataset's own module:
// the catalogue only lists what the caller may read, and a run against anything else
// comes back as though the dataset did not exist. Signed in is still required — without
// it there are no permissions to resolve and every dataset resolves to nothing.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/reports")]
[Authorize]
public class ReportsController(IReportService reports) : ApiControllerBase
{
    // What the caller may report on, with each dataset's columns and how many records
    // it holds for them. The count is run through the same provider the report uses, so
    // it is the number of rows they would actually get rather than the size of the
    // table.
    [HttpGet("datasets")]
    [ProducesResponseType<IReadOnlyList<ReportDatasetDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Datasets(CancellationToken cancellationToken) =>
        Ok(await reports.DatasetsAsync(cancellationToken));

    // A page of rows.
    //
    // POST rather than GET because the body carries a column list and a filter map, and
    // a report with a dozen filters does not belong in a query string — not for length,
    // but because the values are somebody's search terms and a URL is the one part of a
    // request that ends up in logs and browser history.
    [HttpPost("run")]
    [ProducesResponseType<ReportTableDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Run(
        ReportRunRequest request,
        CancellationToken cancellationToken)
    {
        ReportRunResult result = await reports.RunAsync(request, cancellationToken);

        if (result.Succeeded)
        {
            return Ok(result.Table!);
        }

        return result.Outcome switch
        {
            ReportOutcome.UnknownDataset => Failure<ReportTableDto>(
                MoteeStatusCodes.NotFound, "There is no report by that name."),

            ReportOutcome.NotFilterable => Failure<ReportTableDto>(
                MoteeStatusCodes.InvalidRequest,
                $"This report cannot be filtered by '{result.Column}'."),

            _ => Failure<ReportTableDto>(
                MoteeStatusCodes.InvalidRequest, "That report could not be run."),
        };
    }

    // The same report as a file.
    //
    // The answer is a queued job, not a spreadsheet: the caller polls GET /exports/{id}
    // or picks it up from GET /exports/mine, and the file arrives by email as a signed
    // link when it is built. Same response shape as POST /employees/export, so a client
    // handles both the same way.
    //
    // No page size on the request. An export is the whole result, and a report of
    // 40,000 rows is exactly the one somebody wants as a file rather than a grid.
    [HttpPost("export")]
    [ProducesResponseType<ExportJobDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Export(
        ReportExportRequest request,
        CancellationToken cancellationToken)
    {
        ReportExportResult result = await reports.ExportAsync(request, cancellationToken);

        if (result.Succeeded)
        {
            return CreatedEnvelope(
                result.Job!, "Export started. We will email you when it is ready.");
        }

        return result.Outcome switch
        {
            ReportOutcome.UnknownDataset => Failure<ExportJobDto>(
                MoteeStatusCodes.NotFound, "There is no report by that name."),

            ReportOutcome.NotFilterable => Failure<ExportJobDto>(
                MoteeStatusCodes.InvalidRequest,
                $"This report cannot be filtered by '{result.Column}'."),

            _ => Failure<ExportJobDto>(
                MoteeStatusCodes.InvalidRequest, "That report could not be exported."),
        };
    }
}
