# Staging — how the deployed environment actually works

The environment the frontend builds against. One EC2 instance running the API in Docker
behind nginx, with Postgres on the same host.

Not production, and deliberately not a template for it — see [what production still
needs](production-checklist.md).

---

## The pieces

| Piece | What it is |
|---|---|
| Host | EC2 `i-0644204d7c872674f`, `eu-west-2`, elastic IP `18.168.141.87` |
| API | Docker container `backend-api`, `network_mode: host`, port 8080 |
| Registry | ECR `motee-api-dev` |
| Proxy | nginx, TLS from certbot, `api.develop.moteesolutionsltd.com` |
| Database | Postgres **on the instance**, database `motee`, role `motee` |
| Jobs | Hangfire, same process, its own `hangfire` schema |
| Email | Resend, sending domain `moteesolutionsltd.com` |
| DNS | Route 53 (registrar is Namecheap; the nameservers point at AWS) |
| Logs | CloudWatch `/ec2/dev/backend/hr` |

Public URLs:

- `https://api.develop.moteesolutionsltd.com/swagger`
- `https://api.develop.moteesolutionsltd.com/jobs` — background jobs, basic auth
- `/health` liveness, `/health/ready` database check

Port 8080 is **not** open in the security group. Everything arrives through nginx.

---

## Deploying

Push to `develop`. The workflow builds, pushes to ECR, then drives the instance over SSM
to pull and recreate the container. No source is ever cloned onto the server, and
nothing in the pipeline touches the database.

Only two files live on the instance: `~/backend/docker-compose.yml` and `~/backend/.env`.

To deploy by hand:

```bash
cd ~/backend
aws ecr get-login-password --region eu-west-2 \
  | docker login --username AWS --password-stdin <account>.dkr.ecr.eu-west-2.amazonaws.com
docker compose pull && docker compose up -d --force-recreate
```

---

## Reaching the instance

**SSH** needs your current address in the security group, and consumer ISPs reassign
frequently — expect to update it often:

```bash
curl -s https://checkip.amazonaws.com          # your address now
ssh -i ~/.ssh/MOTEE-DEVELOPMENT-SERVER.pem ubuntu@18.168.141.87
```

**SSM port forwarding** avoids that entirely and needs no open port. It authenticates
with your AWS credentials rather than your location, so it keeps working when your IP
moves:

```bash
aws ssm start-session --target i-0644204d7c872674f \
  --document-name AWS-StartPortForwardingSession \
  --parameters '{"portNumber":["5432"],"localPortNumber":["15432"]}'
```

Then point psql or DBeaver at `localhost:15432` — database `motee`, user `motee`, no SSH
tab. Postgres itself stays bound to localhost on the instance and is never exposed.

---

## Schema

Applied by hand from a DDL dump. **There are no migrations in the repository** — the
folder was deliberately removed once the decision was made to manage DDL directly.

To produce DDL for a change, add a migration temporarily, generate the script, then
remove it:

```bash
dotnet ef migrations add Change \
  --project src/Motee.Infrastructure --startup-project src/Motee.Api \
  --output-dir Persistence/Migrations

dotnet ef migrations script --idempotent \
  --project src/Motee.Infrastructure --startup-project src/Motee.Api \
  --output deploy/schema.sql

dotnet ef migrations remove \
  --project src/Motee.Infrastructure --startup-project src/Motee.Api
```

Then apply `deploy/schema.sql` through the tunnel:

```bash
psql -h localhost -p 15432 -U motee -d motee -f deploy/schema.sql
```

**The gap this leaves.** Nothing detects an entity changed without matching DDL. It
surfaces in production as `column ... does not exist` on whichever endpoint touches it
first, with nothing saying the schema is out of date.

`dotnet ef migrations has-pending-model-changes` cannot help here: with no migrations
and no snapshot it reports drift unconditionally, so it is noise rather than a signal.
Getting a real check back means either keeping migrations in the repository, or
committing a snapshot to diff against. Until then, schema drift is caught by a person
remembering.

---

## Configuration

Everything is environment variables in `~/backend/.env`; `__` is .NET's nesting
separator and `__0`, `__1` are array indices. See `deploy/.env.example` for the annotated
list.

The ones that waste an afternoon when wrong:

| Variable | Why |
|---|---|
| `Cors__Origins__0` | Unset blocks every browser request, which reads to the frontend as a dead backend rather than a rejection |
| `App__BaseUrl` | The **web app**, not the API. Emailed links are built from it |
| `JwtSettings__SigningKey` | Must be ≥32 bytes. A placeholder left in place is an 11-byte key, and the failure appears at a user's first sign-in, not at boot |
| `Resend__SenderEmail` | Must be `@moteesolutionsltd.com` — the verified domain, **not** the `send.` subdomain, which is only the bounce path |
| `Network__TrustedProxies__0` | `127.0.0.1`. Without it `X-Forwarded-Proto` is not authoritative and audit rows record nginx's address |

Generate secrets on the instance rather than pasting them in:

```bash
openssl rand -base64 48
```

---

## What is deliberately different here from production

**`Logging__RequestBodies=true`.** Every request and response body goes to CloudWatch.
Passwords, OTP codes and join tokens are redacted; everything else is verbatim. Fine
while the data is invented — turn it off before it is not.

**Swagger is on.** It is gated on `!IsProduction()`, and the schemas describe salary and
personal-data shapes.

**The jobs dashboard is reachable.** Basic auth over TLS, and the password is a real
credential: job arguments contain rendered emails, so anyone who can open `/jobs` can
read live OTP codes and one-time join links.

**What is *not* different:** the fixed `123456` OTP does not work here, and invite
responses do not carry `joinToken`. Both are Development-only, and the OTP bypass is
compiled out of Release builds entirely — so no environment variable on a server can
switch it on. Staging exercises the real flows.

---

## When something breaks

`docker compose logs` returns nothing by design — the awslogs driver sends straight to
CloudWatch, so logs outlive the container.

```bash
aws logs tail /ec2/dev/backend/hr --since 15m --follow --region eu-west-2
```

| Symptom | Usual cause |
|---|---|
| Deploy reports failure at the health check | container exited; read CloudWatch, not `docker compose ps` |
| Everything 500s with `relation ... does not exist` | schema not applied |
| Frontend sees "backend down", curl works | origin missing from `Cors__Origins` |
| Emails not arriving | check the error line — it names the sender address and Resend's reason |
| 500 at the last step of sign-up | signing key too short |
| DBeaver tunnel times out | your IP moved; update the security group, or use SSM |
