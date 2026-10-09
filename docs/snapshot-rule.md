# Records of decisions store what they decided on

A record of something that happened stores the values it happened against. It does not
store a pointer to live data and recompute.

This is already how the codebase works in several places. It is written down because
payroll is the case where getting it wrong is not a display bug but a legal one, and
because the pull towards a foreign key is strongest exactly there.

## The rule

When you persist the outcome of a decision — an approval, a booking, a calculation, a
payment — copy the inputs onto the record. Reading that record later must not touch the
tables the inputs came from.

A foreign key is correct for *what this relates to*. It is wrong for *what this was
decided against*.

## Why

Reference data changes, and it changes for reasons that have nothing to do with the
records already decided against it.

Somebody's salary rises in October. Their September payslip must still show September's
figures. If `PayrollRunEmployee` holds `EmployeeId` and joins to `Employee.Salary`, then
the moment HR records a pay rise, every historical payslip silently restates itself. The
payslip was issued, filed, and possibly used in a mortgage application. It cannot change.

The same shape, less dramatically:

- A leave policy is raised from 20 days to 25. Leave already booked must not re-cost.
- An approval chain gains a step. An approval already in flight must not sprout one.
- A department is renamed. An audit entry must still say what it said at the time.

In each case the recomputed answer is not merely different — it is a claim that something
happened which did not.

## Where this already applies

| Record | What it snapshots | Why |
|---|---|---|
| `ApprovalStepInstance` | `Label`, `Approver`, `RoleId`, `Required` | A template edited mid-flight must not change what somebody was asked |
| `LeaveRequest` | `TotalDays`, computed at submission | Changing a policy or a public holiday must not re-cost booked leave |
| `AuditEntry` | `Changes`, old and new values as text | The point of the record is what the values were |
| `ApprovalAttachment` | `Round` | Evidence that arrived on the second round must read as arriving late |

The approval engine states the reasoning in `ApprovalTemplate.cs`: the snapshot is what
makes editing a template safe. That is the general case. Snapshotting is not defensive
duplication; it is what lets reference data stay editable at all.

## Where it does not apply

Not everything should be frozen. The distinguishing question is whether the record is a
decision or a description.

- **Decision** — a payslip, an approval, a booked absence. Snapshot.
- **Description** — "this employee is in Engineering". Point at the department; a rename
  should be reflected everywhere.

A role step on an approval is a useful edge: the *role id* is snapshotted, because which
queue was asked is part of the decision. Who currently holds that role is read live,
because the queue should follow the people actually in the job. Freeze the question, not
the answer.

## Applying it to payroll

`PayrollRunEmployee` is the snapshot row. It carries, as its own columns:

```
EmployeeId              -- the relation, for grouping and lookup
EmployeeNameSnapshot    -- what the payslip must print
GradeSnapshot
BasicSalary
Allowances              -- itemised, not a total
GrossPay
TaxableIncome
Tax
Pension
OtherDeductions
NetPay
TaxRuleVersion          -- which rules produced these figures
```

Two things that are easy to miss:

**Snapshot the rule version, not just the amounts.** When a tax band changes, you need to
answer "why was this person taxed that much in September" — and the amount alone does not
answer it. A `TaxRuleVersion` makes a historical payslip explainable rather than merely
preserved.

**Itemise, do not total.** A single `Allowances` figure cannot be explained to somebody
disputing it. Store the components.

Reading a payslip must be a read of `PayrollRunEmployee` and nothing else. If displaying a
September payslip requires joining to `Employee`, the design is wrong, and it will be
wrong quietly — correct until the first pay rise.

## How to check it

For any record that represents a decision, ask: if every row in the tables it references
changed tomorrow, would this record still say the same thing? If not, the inputs belong on
the record.

A test is worth more than care here. The shape:

1. Create the record.
2. Change the reference data it was decided against.
3. Read the record back and assert it is unchanged.

`LeaveRequestTests` and `ApprovalEngineTests` both have tests in this shape; payroll should
have one per snapshotted figure.
