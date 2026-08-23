# Production TODO — CloudFront + environment variables

Things that are **configured in code but not yet done in AWS**. Nothing here is
needed for local development; all of it is needed before the deployed app behaves
correctly.

---

## 1. CloudFront — visitor country detection

`GET /api/locale/detect` pre-selects the country toggle on the login / sign-up
cards. It reads the viewer's country from a CDN edge header, so there is no lookup
cost, no latency, and no third party ever sees a visitor's IP.

Behind a bare load balancer no header arrives and **every visitor silently falls
back to `GB`**. CloudFront is what makes it work.

### 1.1 Create the distribution

- Origin: the ALB's DNS name, protocol **HTTPS only**
- Add a **custom origin header**:
  `X-Origin-Verify` = a long random string (see §2, `Cdn__OriginVerifySecret`)

### 1.2 Configure the behaviour — the step that actually delivers the country

| Setting | Value |
|---|---|
| Cache policy | `Managed-CachingDisabled` |
| Origin request policy | `Managed-AllViewerAndCloudFrontHeaders-2022-06` |

> **This is the step that is easy to get wrong.** CloudFront does **not** forward
> `CloudFront-Viewer-Country` by default. With the plain `Managed-AllViewer`
> policy the header never reaches the origin and detection silently returns the
> `GB` fallback for everyone. A custom origin request policy is fine too, as long
> as `CloudFront-Viewer-Country` is in its header list.

Keep the country header **out of the cache policy**. Caching is disabled so it is
moot today, but if caching is enabled later, putting it in the cache key
fragments the cache per country.

### 1.3 Lock the ALB to CloudFront

Without this the distribution is decorative — anyone can hit the ALB directly and
forge `CloudFront-Viewer-Country`.

- ALB security group inbound: source = AWS managed prefix list
  `com.amazonaws.global.cloudfront.origin-facing` (**not** `0.0.0.0/0`)
- `Cdn__OriginVerifySecret` is defence in depth behind that. The app ignores all
  geo headers unless the request carries the matching `X-Origin-Verify` value.

### 1.4 Verify after deploying

```bash
curl -s https://<distribution>.cloudfront.net/api/locale/detect
```

- Expect `"source": "cdn-header"` and a real country.
- `"source": "default"` → the origin request policy is wrong (§1.2).
- A country returned when hitting the **ALB directly** → the security group is
  not locked down (§1.3).

> Detection **fails closed**: a missing or wrong secret means everyone gets `GB`
> rather than a spoofable value. That is the right direction to fail, but it is
> silent — run this check on every deploy.

---

## 2. Environment variables

`appsettings.json` is git-ignored, so deployed environments are configured
entirely through environment variables. Double underscore (`__`) is the .NET
nesting separator; `__0`, `__1` … are array indices.

Set these in the ECS task definition.

### Required

| Variable | Example | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Also gates Swagger (§3) and HTTPS redirection |
| `ConnectionStrings__Default` | `Host=…;Port=5432;Database=motee;Username=…;Password=…` | Use Secrets Manager, not a literal |
| `JwtSettings__SigningKey` | *(long random string)* | Until set, every presented token fails validation |
| `Cdn__OriginVerifySecret` | *(long random string)* | **Must equal** the CloudFront custom origin header from §1.1 |
| `Network__TrustedProxies__0` | `10.0.0.0/16` | Your VPC CIDR. Empty ⇒ loopback only ⇒ every audit row records the ALB's IP |
| `Network__ForwardLimit` | `2` | See the warning below |
| `App__Debug` | `false` | See the warning below — this one is dangerous |
| `App__BaseUrl` | `https://app.motee.com` | The **web app**, not the API. Emailed invite links are built from it; unset, inviting an employee throws |
| `Cors__Origins__0` | `https://app.motee.com` | Browser origins allowed to call the API. **Unset means every browser request is blocked**, which reads as "the backend is down". `https://*.sub.domain` matches one level of subdomain, for preview deployments — never point that at a domain anyone can deploy to when real records are in the database |
| `Email__Provider` | `resend` | `none` (default) drops every email with a warning log |
| `Resend__ApiKey` | *(secret)* | Required when `Email__Provider=resend` |
| `Resend__SenderEmail` | `no-reply@motee.app` | Must be on a domain verified in Resend |

> ⚠️ **`App__Debug=true` makes `123456` a valid OTP for every account.** It is
> ignored when `ASPNETCORE_ENVIRONMENT=Production`, so production is safe even if
> the flag is set — but **staging normally runs as `Development`**, where the flag
> is honoured. Set it to `false` in every deployed environment.
>
> **`ForwardLimit` must be `2` behind CloudFront.** The chain is
> client → CloudFront → ALB, so `X-Forwarded-For` arrives as
> `client, cloudfront-edge` and the middleware has two hops to walk back. Leave
> it at `1` and every audit row records a CloudFront edge IP instead of the real
> visitor. Direct ALB with no CDN = `1`.

### Optional

| Variable | Default | Notes |
|---|---|---|
| `Locale__DefaultCountry` | `GB` | Fallback when the visitor's country is undetectable or unsupported |
| `Swagger__Enabled` | unset | Unset ⇒ on outside Production. `true` exposes docs in a deployed environment (staging); `false` forces off |
| `Serilog__MinimumLevel__Default` | `Information` | |
| `Serilog__MinimumLevel__Override__Microsoft.AspNetCore` | `Warning` | Framework noise. Per-request lines come from Serilog's own logger and survive this |
| `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command` | `Information` | Logs every SQL statement — consider `Warning` in production |
| `Serilog__MinimumLevel__Override__Motee.Application.Localization` | `Information` | Drop to `Warning` if the per-visitor country log gets noisy |
| `Resend__SenderName` | `Motee` | Display name on outgoing mail |
| `Hangfire__WorkerCount` | `4` | Background workers per instance |

### Secrets

`ConnectionStrings__Default`, `JwtSettings__SigningKey`,
`Cdn__OriginVerifySecret` and `Resend__ApiKey` are secrets. Store them in AWS Secrets Manager or SSM
Parameter Store and reference them from the task definition's `secrets` block —
never as plain `environment` entries, which are readable by anyone with
`ecs:DescribeTaskDefinition`.

---

## 2b. S3 — file storage

Every uploaded file and every generated export lives in one bucket: company logos,
employee avatars, employee documents, and employee CSV exports. There is **no local
disk fallback** — two ECS tasks would each write to their own volume and roughly half
the downloads would 404, a failure that only appears once deployed.

### Bucket

- [ ] Create the bucket, **Block Public Access on**. Nothing is world-readable; the
      only way in is a signed link.
- [ ] **Exports are delivered as presigned links by email**, valid for
      `ExportPolicy.LinkLifetime` (24h). That link is unauthenticated — anyone the
      email reaches can open it until it expires, which is the accepted trade for
      one-click download. Uploaded files are different: they stream through
      `GET /api/v1/files/{id}/download`, which re-checks permissions and tenant.
- [ ] Default encryption **SSE-S3 (AES256)**. The upload sets it per object as well.
- [ ] Versioning on, so an overwritten or deleted object is recoverable.
- [ ] Lifecycle rule: expire `exports/` after **30 days**. `ExportPolicy.Retention`
      stops serving them after 7; the rule is what actually removes the bytes.
      Exports are a snapshot of everyone's personal data — they must not accumulate.

### Access

- [ ] The ECS **task role** gets `s3:PutObject`, `s3:GetObject`, `s3:DeleteObject` on
      `arn:aws:s3:::<bucket>/*` and nothing else. No access keys in configuration —
      `AddAWSService<IAmazonS3>()` picks up the task role automatically.

| Variable | Example | Notes |
|---|---|---|
| `Storage__Bucket` | `motee-files-prod` | Required. Uploads and exports both fail without it |
| `AWS__Region` | `eu-west-1` | Same region as the tasks, or every upload pays a cross-region hop |

---

## 3. Other production checks

- [ ] **Swagger** — off in Production by default. Decide deliberately whether
      staging exposes it; the schemas describe salary and PII shapes.
- [ ] **Migrations** — `dotnet ef database update` is not part of the deploy
      pipeline yet. Decide whether migrations run as a pre-deploy task or by hand.
- [ ] **HTTPS redirection** — skipped in Development; active elsewhere. Confirm
      the ALB terminates TLS and forwards `X-Forwarded-Proto`.
- [ ] **There is no audit trail yet.** Nothing records who changed an employee
      record, so the History and Change Request Log screens have no source. When one
      is built it will store IP addresses — personal data under NDPR/GDPR — so agree
      a retention period at the same time.
- [ ] **Hangfire dashboard** — `/jobs` is open to anyone who can reach the app.
      Job arguments include email addresses. Lock it down or turn it off.
- [ ] **`App__Debug` must be false.** With it true outside Production, `123456`
      verifies any account. The code refuses to apply it when
      `ASPNETCORE_ENVIRONMENT=Production`, so the real risk is a **staging**
      environment that is internet-reachable.
- [ ] **Export sweep** — `ExportPolicy.Retention` stops the API serving an expired
      export, but nothing deletes the object yet. The S3 lifecycle rule above is the
      backstop; a job that also removes the `export_jobs` row would be tidier.
- [ ] **Special-category data retention.** `employee_medical` holds health data and
      `employee_identity_documents` holds passport and national ID numbers. Both are
      stored in plaintext, restricted by permission — medical behind
      `employee.medical`, which only SuperAdmin and HrAdmin hold. Agree a retention
      period for leavers, and decide whether these need encryption at rest before a
      real tenant's data is in them.
- [ ] **Virus scanning** — uploads are checked for size, declared type and magic
      bytes, which stops a renamed executable being stored and served back. It is not
      malware scanning. If employees upload documents that other staff download,
      decide whether that gap is acceptable.


## 4. Email delivery

Nothing is sent until a provider is configured. With `Email:Provider` unset the
API logs a warning per dropped message and carries on, so verification codes and
password resets silently never arrive.

- [ ] Create a Resend account
- [ ] **Verify the sending domain** — DNS records, so start early
- [ ] Set `Email__Provider`, `Resend__ApiKey`, `Resend__SenderEmail`
- [ ] Send one real verification email end to end before opening sign-up

## 5. Hangfire

- [ ] **Its schema is created automatically on first startup** — 12 tables in a
      separate `hangfire` schema. Nothing to run, but it appears without warning.
- [ ] **Secure the dashboard.** `/jobs` is open in Development and closed
      everywhere else. It exposes recipient addresses and message bodies in job
      arguments and allows requeue/delete, so it stays shut until it sits behind
      real authorisation.
- [ ] Decide whether background workers run on the API instances or their own.

## 6. Database

Scripts are applied by hand; there is no migration runner and **no drift
detection**. A stale schema surfaces as a confusing missing-column error rather
than "your schema is out of date".

- [ ] `schema.sql` — base
- [ ] `seed-roles.sql` — without it, registration fails with *"Role HrAdmin does not exist"*
- [ ] `schema-tenant-setup.sql`
- [ ] `schema-onboarding-flag.sql`
- [ ] `schema-access-levels-refresh-tokens.sql`
- [ ] `schema-drop-access-levels.sql`
- [ ] `schema-drop-username.sql`
- [ ] `schema-employees.sql`
- [ ] `schema-role-slugs.sql` — safe from any state, idempotent
- [ ] `schema-company-size.sql`
- [ ] `schema-departments-update.sql`
- [ ] `schema-employment-types-update.sql` — idempotent; drops the seeded table
- [ ] `schema-employees-update.sql`
- [ ] `schema-employee-invitations.sql`
- [ ] `schema-export-jobs.sql`
- [ ] `schema-stored-files.sql`
- [ ] `schema-work-mode.sql` — normalises free-text work modes; idempotent
- [ ] `schema-employee-profile-blocks.sql` — bank, identity and medical tables
- [ ] `schema-assets.sql` — assets module; also repairs the holder FK to RESTRICT
- [ ] `schema-employee-self-onboarding.sql` — avatar column
- [ ] `schema-invitation-purpose.sql` — onboarding vs password-only links
- [ ] Unique index on `users(normalized_email)` — one address, one company
