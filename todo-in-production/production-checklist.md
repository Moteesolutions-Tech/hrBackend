# Production checklist

What still has to be true before this holds a real company's payroll data. The deployed
staging environment is described in [staging-runbook.md](staging-runbook.md).

Ordered by what it costs to get wrong, not by effort.

---

## 1. Nothing is backed up

Postgres runs on the instance's own disk. No snapshots, no dumps, no replica. A
terminated instance, a corrupted volume or a mistaken `DELETE` loses everything, and for
a payroll system that is unrecoverable in a way no other item here is.

- [ ] Automated backups with a retention window — RDS gives this by default, which is
      most of the argument for moving off Postgres-on-the-box
- [ ] EBS snapshots at minimum, if it stays self-managed
- [ ] **Restore it once.** An untested backup is a belief, not a backup

## 2. Secrets are in a plain file

`~/backend/.env` holds the database password, the JWT signing key, the Resend API key
and the jobs dashboard password, in plaintext, readable by anyone who reaches the box.

- [ ] Secrets Manager or SSM Parameter Store, referenced by the task/instance rather
      than written to disk
- [ ] Rotate everything currently in that file — several have passed through terminals,
      clipboards and shell history
- [ ] Separate credentials per environment; staging and production must not share a
      signing key, or a token minted in one is valid in the other

## 3. There is no audit trail

Nothing records who changed an employee record, so the History and Change Request Log
screens have no source, and "who altered this salary" has no answer.

- [ ] Decide what is recorded: actor, tenant, entity, before/after, IP, timestamp
- [ ] Agree a retention period **at the same time** — it stores IP addresses, which are
      personal data under NDPR and GDPR
- [ ] It has to be written where it cannot be skipped; a service that forgets to call it
      is indistinguishable from one that had nothing to record

## 4. Special-category data is plaintext

`employee_medical` holds health information; `employee_identity_documents` holds passport
and national ID numbers. Both are protected by permission checks alone — anyone with
database access reads them directly.

- [ ] Decide whether these need encryption at rest beyond the volume
- [ ] Retention for leavers: how long after someone leaves does their passport number
      stay?
- [ ] Confirm who holds `employee.medical` and that the list is deliberate

## 5. Storage — done, with one design decision left

Staging uses `motee-dev-files`: Block Public Access on, versioning on, SSE-S3, lifecycle
rule expiring `exports/` after 30 days plus noncurrent versions after 1. The instance
role holds `s3:PutObject`, `s3:GetObject`, `s3:DeleteObject` on that bucket and nothing
else — deliberately no `s3:ListBucket`, so nobody on the box can enumerate employee
documents. No keys in configuration.

Production needs the same, in its own bucket, with its own role.

**Still open:**

- [ ] **How exports are delivered.** Today an export is emailed as a presigned link that
      is unauthenticated for its whole lifetime — anyone the email is forwarded to can
      download every employee's personal details. Uploaded files already do this
      properly: `GET /api/v1/files/{id}/download` re-checks permission and tenant on
      every request. Exports should go the same way before real payroll data exists
- [ ] A job that deletes the `export_jobs` row alongside the object, so the record and
      the bytes expire together rather than leaving rows pointing at nothing

**Already fixed:** `LinkLifetime` was 24 hours, which a presigned URL cannot honour — it
cannot outlive the temporary credentials that signed it, and an instance role's rotate
every few hours. Links died mid-afternoon with an opaque `AccessDenied`. Now one hour,
which is a promise the credentials can keep.

## 6. CI runs no tests

The pipeline builds and ships. 723 tests never run, including the ones pinning that the
fixed OTP is unreachable, that invite tokens are withheld, and that registration cannot
be used to enumerate customers.

- [ ] Add `dotnet test` to the workflow
- [ ] A Postgres service container runs all of them; without one roughly 430 run and the
      rest skip — and the skipped ones are the integration tests that catch schema drift
- [ ] `dotnet ef migrations has-pending-model-changes` as a step, so an entity changed
      without a migration fails the build rather than production

## 7. Configuration is only validated when it is used

Three separate incidents in one day, each of which was knowable at boot: a Resend key
scoped to the wrong domain, a sender on an unverified domain, and an 11-byte signing key.
All three surfaced as a user-facing failure hours later.

- [ ] Validate at startup: signing key length, `App:BaseUrl` present, email provider
      credentials, storage bucket if set
- [ ] Fail the start rather than logging — a container that refuses to start is caught by
      the deploy; a warning in CloudWatch is not

## 8. Before opening it up

- [ ] **Swagger** — off in Production by default. Decide deliberately whether any
      deployed environment exposes it; the schemas describe salary and PII shapes
- [ ] **`Logging__RequestBodies`** — off. It writes every payload to CloudWatch
- [ ] **Jobs dashboard** — behind basic auth, but job arguments are rendered emails.
      Consider whether it is exposed at all in production
- [ ] **Rate limiting** — `/auth/register`, `/auth/resend-otp` and `/auth/login` are
      anonymous and unthrottled. Resend can be used to mail-bomb an address
- [ ] **Virus scanning** — uploads are checked for size, declared type and magic bytes,
      which stops a renamed executable being stored and served back. That is not malware
      scanning. If employees download each other's documents, decide whether the gap is
      acceptable

## 9. Product gaps

- [ ] Business unit CRUD — the table and the scope exist; nothing manages them
- [ ] Offboarding as a process rather than a status change
- [ ] `AccessLevel.LastUsedAt` is never written, so "unused levels" cannot be found
- [ ] Unverified tenants are never cleaned up. Every abandoned sign-up permanently holds
      a slug and leaves a tenant, its access levels and a user behind

---

## Optional: CloudFront

`GET /api/locale/detect` pre-selects the country toggle on the sign-up card by reading
`CloudFront-Viewer-Country` at the edge — no lookup cost, no latency, no third party
seeing a visitor's IP. Without a CDN it falls back to `GB` for everyone, which is a
cosmetic default rather than a fault.

If it is added:

- Origin request policy must include `CloudFront-Viewer-Country`. The plain
  `Managed-AllViewer` policy does **not** forward it, and detection silently returns the
  fallback — use `Managed-AllViewerAndCloudFrontHeaders-2022-06`
- Keep the country header out of the *cache* policy, or the cache fragments per country
- Lock the origin to the `com.amazonaws.global.cloudfront.origin-facing` prefix list,
  otherwise anyone can bypass the CDN and forge the header
- Set `Cdn__OriginVerifySecret` to match the custom origin header. The app ignores geo
  headers without it, so detection fails closed — safe, but silent, so verify on deploy
- `Network__ForwardLimit` becomes **2** — client → CloudFront → origin. Left at 1, every
  audit row records a CloudFront edge address instead of the visitor
