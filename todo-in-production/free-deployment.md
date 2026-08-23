# Deploying the free environment

A throwaway environment so the frontend can build against a real API. Not production,
and deliberately not safe enough to become production — see the warning at the end.

---

## Why email is not a blocker

Two things make a no-email deployment fully usable:

- The invite endpoints return `joinToken` in the response whenever the environment is
  **not** Production. The frontend can create an invitation, read the token straight
  out of the response, and drive `/join/{token}` end to end without a mailbox.
- `App__Debug=true` makes `123456` a valid OTP for any account, so login and email
  verification work too.

Set `ASPNETCORE_ENVIRONMENT` to `Staging`, never `Production`, or both of those stop
and nothing can be tested until Resend is configured.

---

## The three pieces

| Piece | Service | Why not the obvious alternative |
|---|---|---|
| API | **Render**, free web service, from the Dockerfile | Azure F1 cannot run custom containers; Fly has no real free tier |
| Database | **Neon**, free tier | Render's own free Postgres expires after 30 days, so you would migrate again in a month |
| Files | **Cloudflare R2**, 10 GB free | Optional — see below |

Render's free service sleeps after roughly 15 minutes idle and takes around 50
seconds to wake. Two consequences worth telling the frontend team about: the first
request of the morning looks like a timeout, and **Hangfire does not run while the
service is asleep**, so a queued export simply waits until something wakes the API.

---

## Storage is optional now

With `Storage__Bucket` unset, the AWS client is never constructed and file storage
falls back to a stub. Uploads and CSV exports fail with a message saying storage is
not configured; **everything else works normally.**

This was not true until recently. Building the AWS client resolves credentials
eagerly, and storage is a dependency of the employee service (avatar links) and the
invitation service (the joiner's photo) — so with no credentials, every employee and
join request failed with an unhandled AWS error. It read as a completely broken
backend. Worth knowing, because the same shape of problem returns whenever a widely
used service resolves an external dependency in its constructor.

### Turning storage on with R2

```
Storage__Bucket=motee-dev
Storage__ServiceUrl=https://<account-id>.r2.cloudflarestorage.com
Storage__AccessKey=<R2 access key id>
Storage__SecretKey=<R2 secret access key>
Storage__Region=auto
```

Setting `Storage__ServiceUrl` switches the client from Amazon's credential chain —
environment, profile, instance metadata, none of which mean anything to Cloudflare —
to the explicit keys above, and turns on path-style addressing, because
bucket-as-subdomain only resolves against Amazon's own domains.

Leave `Storage__ServiceUrl` unset on AWS. The task role supplies credentials there and
there are no keys to store.

One difference worth knowing: uploads to S3 ask for SSE-AES256 explicitly, and to a
compatible store they do not. R2 encrypts at rest unconditionally and rejects the
header as unsupported, so sending it would fail the upload while changing nothing
about the outcome.

---

## Environment variables

```
ASPNETCORE_ENVIRONMENT=Staging
ConnectionStrings__Default=Host=...neon.tech;Database=motee;Username=...;Password=...;SSL Mode=Require
JwtSettings__SigningKey=<32+ random bytes>
App__BaseUrl=https://<frontend>.vercel.app
App__Debug=true
Cors__Origins__0=https://<frontend>.vercel.app
Cors__Origins__1=https://*.vercel.app
Email__Provider=none
```

`Cors__Origins` is the one that will waste an afternoon if forgotten: with nothing
configured every browser request is blocked, and the frontend sees what looks like a
dead backend rather than a rejection.

---

## Health endpoints

| Path | Answers | Use |
|---|---|---|
| `/health` | Is the process alive? Runs no checks | Point the platform's health check here |
| `/health/ready` | Can it reach the database? | Diagnosing, and any load balancer that drains |

Point Render at `/health`, not `/health/ready`. Neon autosuspends, and a platform that
restarts the container every time the database is asleep never lets it come back.

---

## Order of operations

1. Create the Neon database and copy its connection string
2. Apply the schema: `psql "<neon connection string>" -v ON_ERROR_STOP=1 -f schema-apply-all.sql`
3. Create the Render service from the Dockerfile, health check path `/health`
4. Set the environment variables above
5. Check `/health/ready` returns `Healthy` — if not, it is the connection string
6. Open `/swagger` and try `POST /api/v1/auth/register`

Swagger is served outside Production by default, so it is available here. That is
useful for the frontend and is also a reason this environment must not hold anything
real.

---

## The warning

`App__Debug=true` on a public URL means **anyone who finds it can sign in as anyone
with `123456`.** Combined with Swagger being open and the Hangfire dashboard being
reachable in non-production, this environment should be treated as public.

Put invented data in it. Agree now that it gets deleted rather than promoted, because
the settings that make it convenient are exactly the ones that make it unsafe.
