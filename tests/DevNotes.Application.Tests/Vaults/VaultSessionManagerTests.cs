using DevNotes.Application.Vaults;
using DevNotes.Domain.Vaults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DevNotes.Application.Tests.Vaults;

public sealed class VaultSessionManagerTests
{
    private readonly IVaultSessionFactory _factory = Substitute.For<IVaultSessionFactory>();
    private readonly VaultSessionManager _manager;
    private readonly Vault _first = new(VaultId.Parse("v1"), "First", Path.GetTempPath());
    private readonly Vault _second = new(VaultId.Parse("v2"), "Second", Path.GetTempPath());

    public VaultSessionManagerTests()
    {
        _manager = new VaultSessionManager(_factory);
        _factory.Create(Arg.Any<Vault>()).Returns(call =>
        {
            var session = Substitute.For<IVaultSession>();
            session.Vault.Returns(call.Arg<Vault>());
            return session;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenAsync_StartsSessionAndPublishesIt()
    {
        IVaultSession? notified = null;
        _manager.CurrentChanged += (_, session) => notified = session;

        var session = await _manager.OpenAsync(_first, Ct);

        await session.Received(1).StartAsync(Ct);
        _manager.Current.Should().BeSameAs(session);
        notified.Should().BeSameAs(session);
    }

    [Fact]
    public async Task OpenAsync_AnotherVault_DisposesThePreviousSessionFirst()
    {
        var first = await _manager.OpenAsync(_first, Ct);

        var second = await _manager.OpenAsync(_second, Ct);

        await first.Received(1).DisposeAsync();
        _manager.Current.Should().BeSameAs(second);
        second.Vault.Should().Be(_second);
    }

    [Fact]
    public async Task OpenAsync_StartFails_DisposesTheNewSessionAndLeavesNoCurrent()
    {
        var broken = Substitute.For<IVaultSession>();
        broken.StartAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("index is corrupt"));
        var factory = Substitute.For<IVaultSessionFactory>();
        factory.Create(_first).Returns(broken);
        await using var manager = new VaultSessionManager(factory);

        var act = () => manager.OpenAsync(_first, Ct);

        await act.Should().ThrowAsync<IOException>();
        await broken.Received(1).DisposeAsync();
        manager.Current.Should().BeNull();
    }

    [Fact]
    public async Task CloseAsync_DisposesAndNotifiesOnlyWhenSomethingWasOpen()
    {
        var notifications = new List<IVaultSession?>();
        _manager.CurrentChanged += (_, session) => notifications.Add(session);

        await _manager.CloseAsync();
        notifications.Should().BeEmpty();

        var session = await _manager.OpenAsync(_first, Ct);
        await _manager.CloseAsync();

        await session.Received(1).DisposeAsync();
        _manager.Current.Should().BeNull();
        notifications.Should().HaveCount(2).And.EndWith((IVaultSession?)null);
    }

    [Fact]
    public async Task DisposeAsync_ClosesCurrentSession_IsIdempotent_AndBlocksFurtherUse()
    {
        var session = await _manager.OpenAsync(_first, Ct);

        await _manager.DisposeAsync();
        await _manager.DisposeAsync();

        await session.Received(1).DisposeAsync();
        await FluentActions.Invoking(() => _manager.OpenAsync(_second, Ct)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void AddDevNotesApplication_RegistersEveryApplicationService()
    {
        var services = new ServiceCollection().AddDevNotesApplication();

        services.Select(descriptor => descriptor.ServiceType.Name).Should().Contain(
        [
            nameof(TimeProvider),
            "INoteIdGenerator",
            "ISettingsService",
            nameof(IVaultRegistry),
            nameof(IVaultSessionFactory),
            nameof(IVaultSessionManager),
        ]);
        services.Should().OnlyContain(descriptor => descriptor.Lifetime == ServiceLifetime.Singleton);
    }
}
