using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Infrastructure.Identity;

namespace Motee.Infrastructure.Persistence.Configurations;

// Identity's AspNet* table names renamed to match the rest of the schema.

// Identity's role tables are gone. Permissions are tenant-owned access levels now, so
// a global table of ten role names had nothing left to say — and a user held exactly
// one row in it, joined on every login purely to read a name back.

internal sealed class UserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder) =>
        builder.ToTable("user_claims");
}

internal sealed class UserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder) =>
        builder.ToTable("user_logins");
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder) =>
        builder.ToTable("user_tokens");
}
