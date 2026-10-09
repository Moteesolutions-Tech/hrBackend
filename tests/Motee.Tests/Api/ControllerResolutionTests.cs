using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Motee.Api.Controllers;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Every controller must be constructible from the API's own container.
//
// This is the one failure the rest of the suite cannot see. A controller whose
// dependency was never registered compiles, passes every service test, and returns 401
// to an unauthenticated probe — because authorization runs before construction, so the
// controller is never built. The first person to hit it with a valid token gets a 500,
// and that person is in production.
//
// Written over the assembly rather than a hand-kept list, so a controller added next
// month is covered without anybody remembering to add it here.
[Collection(PostgresCollection.Name)]
public class ControllerResolutionTests(PostgresFixture fixture)
{
    public static TheoryData<Type> Controllers()
    {
        TheoryData<Type> data = [];

        foreach (Type type in typeof(OnboardingController).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsPublic: true })
            .OrderBy(type => type.Name))
        {
            data.Add(type);
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Controllers))]
    public void EveryControllerCanBeBuilt(Type controller)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory factory = new(fixture.ConnectionString!);

        // Forces the host to build, which is what creates the container.
        factory.CreateClient();

        using IServiceScope scope = factory.Services.CreateScope();

        ConstructorInfo constructor = Assert.Single(controller.GetConstructors());

        object?[] arguments =
        [
            .. constructor.GetParameters().Select(parameter =>
                scope.ServiceProvider.GetService(parameter.ParameterType)
                    ?? throw new InvalidOperationException(
                        $"{controller.Name} needs {parameter.ParameterType.Name}, "
                        + "which nothing registers.")),
        ];

        Assert.NotNull(constructor.Invoke(arguments));
    }

    // The list has to be non-empty, or the theory above passes by testing nothing —
    // which is exactly how a reflection-driven guard quietly stops guarding.
    [Fact]
    public void TheControllerListIsNotEmpty() =>
        Assert.NotEmpty(Controllers());
}
