using Motee.Domain.Identity;

namespace Motee.Domain.Workflows;

// One seeded piece of work.
//
// Dependencies are step numbers, from one, because that is how they read: "this
// waits on step 9". They are resolved to ids when the workflow is written.
public sealed record WorkflowSeedTask
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required Role Assignee { get; init; }

    public Role? Reviewer { get; init; }

    public int? DueDayOffset { get; init; }

    public int? ExpectedDurationDays { get; init; }

    public WorkflowTaskPriority Priority { get; init; } = WorkflowTaskPriority.Normal;

    public int? EscalateAfterDays { get; init; }

    public IReadOnlyList<int> DependsOnSteps { get; init; } = [];

    public string? ParallelGroup { get; init; }

    public WorkflowCondition? Condition { get; init; }

    // False where the task applies when the fact is absent — "unless a
    // contractor".
    public bool ConditionHolds { get; init; } = true;
}

public sealed record WorkflowSeed
{
    // Stable, so re-seeding an existing company finds what is already there
    // rather than writing a second copy.
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required WorkflowTriggerEvent TriggerEvent { get; init; }

    public required IReadOnlyList<WorkflowSeedTask> Tasks { get; init; }
}

// The four workflows the product drives itself: recruitment, preboarding,
// onboarding and offboarding.
//
// Seeded as System, so a company cannot delete the flows its own recruitment
// and onboarding screens fire. They start Active — unlike a workflow somebody
// writes, which starts as a draft — because the events that trigger them are
// already live on day one.
public static class WorkflowSeeds
{
    public static IReadOnlyList<WorkflowSeed> All =>
        [Recruitment, Preboarding, Onboarding, Offboarding];

    // From an approved requisition to a signed acceptance. The last step is the
    // seam: it hands over to preboarding, whose own trigger fires when a
    // candidate is hired.
    public static WorkflowSeed Recruitment => new()
    {
        Key = "recruitment",
        Title = "Standard Recruitment",
        Description =
            "From an approved requisition to a signed acceptance, then handover to "
            + "preboarding.",
        TriggerEvent = WorkflowTriggerEvent.RecruitmentInitiated,
        Tasks =
        [
            new()
            {
                Title = "Confirm budget & headcount approval",
                Description =
                    "Verify the role is funded and the headcount signed off before anything "
                    + "is advertised.",
                Assignee = Role.Finance,
                Reviewer = Role.HrManager,
                DueDayOffset = 1,
                ExpectedDurationDays = 1,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 2,
            },
            new()
            {
                Title = "Finalise job description & scorecard criteria",
                Description =
                    "Agree the JD, must-have skills and the criteria interviewers will score "
                    + "against.",
                Assignee = Role.LineManager,
                Reviewer = Role.Recruiter,
                DueDayOffset = 2,
                ExpectedDurationDays = 1,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [1],
            },
            new()
            {
                Title = "Publish to selected channels",
                Description =
                    "Post the opening to the job boards and internal channels chosen on the "
                    + "requisition.",
                Assignee = Role.Recruiter,
                DueDayOffset = 3,
                ExpectedDurationDays = 1,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [2],
                ParallelGroup = "sourcing",
            },
            new()
            {
                Title = "Brief agency / referral push",
                Description =
                    "Brief external agencies and open the internal referral window alongside "
                    + "the public posting.",
                Assignee = Role.Recruiter,
                DueDayOffset = 3,
                ExpectedDurationDays = 1,
                DependsOnSteps = [2],
                ParallelGroup = "sourcing",
            },
            new()
            {
                Title = "Screen incoming applications",
                Description =
                    "Work the applicant list against the requisition's filters and reject out "
                    + "of scope early.",
                Assignee = Role.Recruiter,
                DueDayOffset = 10,
                ExpectedDurationDays = 7,
                Priority = WorkflowTaskPriority.High,
                EscalateAfterDays = 3,
                DependsOnSteps = [3],
            },
            new()
            {
                Title = "Shortlist for interview",
                Description = "Hiring manager picks the shortlist from the screened applicants.",
                Assignee = Role.LineManager,
                Reviewer = Role.Recruiter,
                DueDayOffset = 12,
                ExpectedDurationDays = 2,
                Priority = WorkflowTaskPriority.High,
                EscalateAfterDays = 2,
                DependsOnSteps = [5],
            },
            new()
            {
                Title = "Schedule interview panel",
                Description =
                    "Book the panel and send invites with joining details to each shortlisted "
                    + "candidate.",
                Assignee = Role.Recruiter,
                DueDayOffset = 14,
                ExpectedDurationDays = 2,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [6],
            },
            new()
            {
                Title = "Conduct interviews",
                Description = "Run the scheduled rounds with the agreed panel.",
                Assignee = Role.LineManager,
                DueDayOffset = 20,
                ExpectedDurationDays = 6,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 3,
                DependsOnSteps = [7],
            },
            new()
            {
                // Decisions made from memory a week later are how good
                // candidates get lost.
                Title = "Submit scorecards",
                Description =
                    "Every panellist records their scores and recommendation while the "
                    + "interview is fresh.",
                Assignee = Role.LineManager,
                Reviewer = Role.Recruiter,
                DueDayOffset = 21,
                ExpectedDurationDays = 1,
                Priority = WorkflowTaskPriority.High,
                EscalateAfterDays = 2,
                DependsOnSteps = [8],
            },
            new()
            {
                Title = "Reference & background checks",
                Description =
                    "Collect and verify references and run the agreed background checks.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 23,
                ExpectedDurationDays = 3,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [9],
                ParallelGroup = "pre-offer",
            },
            new()
            {
                Title = "Right-to-work pre-check",
                Description =
                    "Confirm the candidate's right to work before an offer is extended, not "
                    + "after.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 23,
                ExpectedDurationDays = 2,
                Priority = WorkflowTaskPriority.Critical,
                DependsOnSteps = [9],
                ParallelGroup = "pre-offer",
                Condition = WorkflowCondition.VisaHolder,
            },
            new()
            {
                Title = "Approve offer & compensation",
                Description =
                    "Sign off the package against the salary band before it reaches the "
                    + "candidate.",
                Assignee = Role.Executive,
                Reviewer = Role.Finance,
                DueDayOffset = 24,
                ExpectedDurationDays = 2,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 2,
                DependsOnSteps = [9],
            },
            new()
            {
                Title = "Extend offer to candidate",
                Description = "Issue the approved offer once checks and sign-off are complete.",
                Assignee = Role.Recruiter,
                Reviewer = Role.HrManager,
                DueDayOffset = 25,
                ExpectedDurationDays = 1,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 2,
                DependsOnSteps = [10, 11, 12],
            },
            new()
            {
                Title = "Candidate accepts offer",
                Description =
                    "Record the acceptance. Closing the requisition and preboarding both wait "
                    + "on this.",
                Assignee = Role.Recruiter,
                DueDayOffset = 30,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 3,
                DependsOnSteps = [13],
            },
            new()
            {
                Title = "Close requisition & notify unsuccessful candidates",
                Description =
                    "Close the opening and send outcomes to everyone still in the pipeline.",
                Assignee = Role.Recruiter,
                DueDayOffset = 31,
                ExpectedDurationDays = 1,
                DependsOnSteps = [14],
            },
            new()
            {
                Title = "Hand over to preboarding",
                Description =
                    "Pass the hire to preboarding with the offer terms and start date.",
                Assignee = Role.HrManager,
                DueDayOffset = 31,
                ExpectedDurationDays = 1,
                Priority = WorkflowTaskPriority.Critical,
                DependsOnSteps = [14],
            },
        ],
    };

    // Before day one: compliance, logistics and data collection. Offer
    // acceptance is an explicit gate, right-to-work is split into upload and
    // verify so the trail shows who checked what, and payroll detail is
    // collected after acceptance rather than bundled with the offer.
    public static WorkflowSeed Preboarding => new()
    {
        Key = "preboarding",
        Title = "Standard Preboarding",
        Description = "Everything between a signed offer and the first morning.",
        TriggerEvent = WorkflowTriggerEvent.PreboardingInitiated,
        Tasks =
        [
            new()
            {
                Title = "Send offer & employment contract",
                Description =
                    "Issue the formal offer and employment contract for the new hire to sign.",
                Assignee = Role.HrManager,
                DueDayOffset = 1,
                ExpectedDurationDays = 1,
                Priority = WorkflowTaskPriority.High,
                EscalateAfterDays = 2,
            },
            new()
            {
                // Nothing downstream should start until the offer is accepted.
                Title = "Candidate accepts offer",
                Description =
                    "Record the candidate's acceptance. Everything downstream waits on this.",
                Assignee = Role.HrManager,
                DueDayOffset = 5,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 3,
                DependsOnSteps = [1],
            },
            new()
            {
                Title = "Collect signed contract",
                Description = "Confirm the signed contract has been returned and filed.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 7,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [2],
            },
            new()
            {
                // Upload and verification are separate steps so the trail shows
                // who checked the documents, not just that they exist.
                Title = "Upload right-to-work documents",
                Description = "New hire submits passport, share code or other RTW evidence.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 8,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [2],
                ParallelGroup = "pre-checks",
            },
            new()
            {
                Title = "HR verifies right-to-work documents",
                Description =
                    "Check the documents against the original and record the verification.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 10,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 2,
                DependsOnSteps = [4],
            },
            new()
            {
                Title = "Immigration verification",
                Description =
                    "Additional checks where the hire's right to work is visa-based.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 12,
                Priority = WorkflowTaskPriority.Critical,
                DependsOnSteps = [5],
                Condition = WorkflowCondition.VisaHolder,
            },
            new()
            {
                Title = "Build employee profile & confirm start date",
                Description =
                    "Create the employee profile from the collected data and confirm the "
                    + "start date.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 12,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [5],
            },
            new()
            {
                // Payroll details are collected after acceptance, close to
                // onboarding, rather than up front with the offer.
                Title = "Capture payroll, bank & tax details",
                Description =
                    "Collect bank, tax and pension details so payroll can be set up.",
                Assignee = Role.Finance,
                DueDayOffset = 14,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [7],
                ParallelGroup = "setup",
            },
            new()
            {
                Title = "Provision IT equipment & accounts",
                Description =
                    "Order the laptop and create email / system accounts ahead of day one.",
                Assignee = Role.ItAdmin,
                DueDayOffset = 14,
                ExpectedDurationDays = 3,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [7],
                ParallelGroup = "setup",
            },
            new()
            {
                Title = "Ship equipment to home address",
                Description = "Courier the laptop and peripherals to a remote worker.",
                Assignee = Role.ItAdmin,
                DueDayOffset = 16,
                DependsOnSteps = [9],
                Condition = WorkflowCondition.RemoteWorker,
            },
            new()
            {
                Title = "Assign company vehicle",
                Description = "Arrange the vehicle, insurance and handover.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 16,
                DependsOnSteps = [7],
                Condition = WorkflowCondition.CompanyCar,
            },
            new()
            {
                Title = "Assign buddy / mentor",
                Description = "Pair the new starter with someone to show them the ropes.",
                Assignee = Role.LineManager,
                DueDayOffset = 15,
                DependsOnSteps = [7],
                ParallelGroup = "manager-prep",
            },
            new()
            {
                Title = "Prepare workspace",
                Description = "Desk, access card and anything else needed on day one.",
                Assignee = Role.LineManager,
                DueDayOffset = 16,
                DependsOnSteps = [7],
                ParallelGroup = "manager-prep",
            },
            new()
            {
                Title = "Schedule induction & first-week objectives",
                Description =
                    "Book the induction sessions and set out what the first week should "
                    + "achieve.",
                Assignee = Role.LineManager,
                DueDayOffset = 17,
                DependsOnSteps = [7],
                ParallelGroup = "manager-prep",
            },
        ],
    };

    // Day one onwards.
    public static WorkflowSeed Onboarding => new()
    {
        Key = "onboarding",
        Title = "Standard Onboarding",
        Description = "Day one through the first weeks, to a settled new starter.",
        TriggerEvent = WorkflowTriggerEvent.OnboardingInitiated,
        Tasks =
        [
            new()
            {
                Title = "Employee completes onboarding forms",
                Description =
                    "Personal, bank, emergency contact and tax details submitted.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 1,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 2,
            },
            new()
            {
                Title = "HR verifies documentation",
                Description = "Check the submitted pack and request changes if needed.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 2,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [1],
            },
            new()
            {
                // Payroll must not finalise before bank details are verified.
                Title = "Payroll approval",
                Description =
                    "Payroll confirms bank and tax details and sets up the record.",
                Assignee = Role.Finance,
                DueDayOffset = 3,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 2,
                DependsOnSteps = [2],
                ParallelGroup = "day-one-setup",
            },
            new()
            {
                // IT cannot activate accounts until HR has approved the record.
                Title = "IT account activation",
                Description = "Enable email, system and building access.",
                Assignee = Role.ItAdmin,
                DueDayOffset = 1,
                Priority = WorkflowTaskPriority.Critical,
                DependsOnSteps = [2],
                ParallelGroup = "day-one-setup",
            },
            new()
            {
                Title = "Equipment issued",
                Description = "Hand over laptop, phone and access card.",
                Assignee = Role.ItAdmin,
                DueDayOffset = 1,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [4],
            },
            new()
            {
                Title = "Manager induction",
                Description = "Team introductions, role expectations and first objectives.",
                Assignee = Role.LineManager,
                DueDayOffset = 2,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [4],
                ParallelGroup = "induction",
            },
            new()
            {
                // Shares the induction group with the task above but waits on
                // nothing, so it starts on day one while that one waits for IT.
                // Carried over as written: the two are grouped on screen rather
                // than gated together.
                Title = "Health & Safety induction",
                Description = "Site safety briefing, fire procedure and DSE assessment.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 3,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 2,
                ParallelGroup = "induction",
            },
            new()
            {
                Title = "Mandatory training assigned",
                Description = "Assign the compliance courses required for the role.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 5,
                DependsOnSteps = [6],
            },
            new()
            {
                // Phrased as "unless a contractor". The client tags this with
                // the contractor condition and a comment saying contractors are
                // not enrolled, which as written enrols only contractors.
                Title = "Benefits enrolment",
                Description = "Pension, healthcare and any role-specific benefits.",
                Assignee = Role.Finance,
                DueDayOffset = 10,
                DependsOnSteps = [3],
                Condition = WorkflowCondition.Contractor,
                ConditionHolds = false,
            },
            new()
            {
                // Sign-off only once the mandatory work is done.
                Title = "HR compliance sign-off",
                Description = "Final HR review that onboarding is complete and compliant.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 20,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [3, 5, 7, 8],
            },
            new()
            {
                Title = "Manager confirms onboarding complete",
                Description =
                    "The hiring manager signs off that the new starter is settled.",
                Assignee = Role.LineManager,
                DueDayOffset = 30,
                DependsOnSteps = [10],
            },
        ],
    };

    // Leavers.
    public static WorkflowSeed Offboarding => new()
    {
        Key = "offboarding",
        Title = "Standard Offboarding",
        Description = "Handover, clearance, final pay and records, through to sign-off.",
        TriggerEvent = WorkflowTriggerEvent.OffboardingInitiated,
        Tasks =
        [
            new()
            {
                Title = "Handover & manager clearance",
                Description = "Complete knowledge handover and obtain manager clearance.",
                Assignee = Role.LineManager,
                DueDayOffset = 5,
                Priority = WorkflowTaskPriority.High,
            },
            new()
            {
                Title = "Exit interview",
                Description = "Capture why they are leaving and what would have kept them.",
                Assignee = Role.HrManager,
                DueDayOffset = 7,
                ParallelGroup = "exit-admin",
            },
            new()
            {
                Title = "Recover company assets",
                Description = "Laptop, phone, access card, keys and any company vehicle.",
                Assignee = Role.ItAdmin,
                DueDayOffset = 1,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [1],
                ParallelGroup = "exit-admin",
            },
            new()
            {
                Title = "Disable system access",
                Description = "Revoke accounts, building access and remote access.",
                Assignee = Role.ItAdmin,
                DueDayOffset = 1,
                Priority = WorkflowTaskPriority.Critical,
                EscalateAfterDays = 1,
                DependsOnSteps = [1],
                ParallelGroup = "exit-admin",
            },
            new()
            {
                Title = "Remove from distribution lists",
                Description = "Mailing lists, shared calendars and internal directories.",
                Assignee = Role.ItAdmin,
                DueDayOffset = 2,
                DependsOnSteps = [4],
            },
            new()
            {
                Title = "Notify Payroll",
                Description = "Tell payroll the leaving date so final pay is calculated.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 3,
                Priority = WorkflowTaskPriority.Critical,
                ParallelGroup = "finance-close",
            },
            new()
            {
                Title = "Notify benefits provider",
                Description = "End pension, healthcare and other benefit memberships.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 5,
                Priority = WorkflowTaskPriority.High,
                ParallelGroup = "finance-close",
            },
            new()
            {
                Title = "Final settlement",
                Description = "Process final pay and settle outstanding balances.",
                Assignee = Role.Finance,
                DueDayOffset = 10,
                Priority = WorkflowTaskPriority.Critical,
                DependsOnSteps = [6],
            },
            new()
            {
                Title = "Final payslip confirmation",
                Description = "Confirm the final payslip has been issued and received.",
                Assignee = Role.Finance,
                DueDayOffset = 14,
                DependsOnSteps = [8],
            },
            new()
            {
                Title = "Reference request status",
                Description =
                    "Record whether a reference has been requested and provided.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 20,
                Priority = WorkflowTaskPriority.Low,
            },
            new()
            {
                Title = "Archive employee records",
                Description =
                    "Move the file to archive with the correct retention period applied.",
                Assignee = Role.HrAdmin,
                DueDayOffset = 25,
                DependsOnSteps = [8],
            },
            new()
            {
                Title = "HR exit sign-off",
                Description = "Confirm the exit checklist is complete and file records.",
                Assignee = Role.HrAdmin,
                Reviewer = Role.HrManager,
                DueDayOffset = 30,
                Priority = WorkflowTaskPriority.High,
                DependsOnSteps = [3, 4, 8, 11],
            },
        ],
    };
}
