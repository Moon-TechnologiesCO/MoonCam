using Microsoft.Extensions.Time.Testing;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;

namespace MoonCam.Core.Tests.Session;

public class PairingCoordinatorTests
{
    private static readonly byte[] PhoneFp = Enumerable.Repeat((byte)7, 32).ToArray();
    private static readonly byte[] DesktopFp = Enumerable.Repeat((byte)9, 32).ToArray();
    private readonly FakeTimeProvider _time = new();
    private readonly MemoryStore _store = new();

    [Fact]
    public void Both_sides_confirming_the_same_code_pairs_the_phone_and_closes_the_window()
    {
        var coordinator = new PairingCoordinator(_store, _time);
        coordinator.OpenWindow();
        var exchange = coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out _)!;

        var phoneNonce = Pairing.NewNonce();
        var desktopNonce = exchange.OnCommit(Pairing.Commit(phoneNonce));
        var code = exchange.OnReveal(phoneNonce);

        // The phone computes the same code from what it saw.
        Assert.Equal(Pairing.Code(PhoneFp, DesktopFp, phoneNonce, desktopNonce), code);
        Assert.Equal(new PairingRequest("Pixel", code), coordinator.Pending);

        exchange.ConfirmOnPhone();
        Assert.False(exchange.IsComplete); // the desktop user has not approved yet
        Assert.True(coordinator.ApprovePending());

        Assert.True(exchange.IsComplete);
        Assert.True(_store.IsPaired(PhoneFp));
        Assert.False(coordinator.IsWindowOpen);
        Assert.Null(coordinator.Pending);
    }

    [Fact]
    public void A_reveal_that_does_not_match_the_commitment_fails_for_good()
    {
        var coordinator = new PairingCoordinator(_store, _time);
        coordinator.OpenWindow();
        var exchange = coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out _)!;
        exchange.OnCommit(Pairing.Commit(Pairing.NewNonce()));

        Assert.Throws<ProtocolException>(() => exchange.OnReveal(Pairing.NewNonce())); // a man in the middle swapping nonces
        Assert.True(exchange.IsFailed);
        Assert.False(coordinator.ApprovePending());
        Assert.Empty(_store.All);
    }

    [Fact]
    public void Pairing_is_refused_when_the_window_is_closed_or_has_expired()
    {
        var coordinator = new PairingCoordinator(_store, _time);
        Assert.Null(coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out var closed));
        Assert.Contains("closed", closed, StringComparison.OrdinalIgnoreCase);

        coordinator.OpenWindow();
        _time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Assert.False(coordinator.IsWindowOpen);
        Assert.Null(coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out _));
    }

    [Fact]
    public void Attempts_are_limited_so_the_code_cannot_be_brute_forced()
    {
        var coordinator = new PairingCoordinator(_store, _time, maxAttempts: 2);
        coordinator.OpenWindow();
        for (var i = 0; i < 2; i++)
            coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out _)!.Fail();

        Assert.Null(coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out var refusal));
        Assert.Contains("Too many", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_one_phone_pairs_at_a_time()
    {
        var coordinator = new PairingCoordinator(_store, _time);
        coordinator.OpenWindow();
        Assert.NotNull(coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out _));

        Assert.Null(coordinator.TryBegin("Other", DesktopFp, DesktopFp, out var refusal));
        Assert.Contains("Another phone", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_desktop_rejecting_the_code_leaves_nothing_paired()
    {
        var coordinator = new PairingCoordinator(_store, _time);
        coordinator.OpenWindow();
        var exchange = coordinator.TryBegin("Pixel", PhoneFp, DesktopFp, out _)!;
        var nonce = Pairing.NewNonce();
        exchange.OnCommit(Pairing.Commit(nonce));
        exchange.OnReveal(nonce);

        coordinator.RejectPending();

        Assert.True(exchange.IsFailed);
        Assert.Throws<ProtocolException>(exchange.ConfirmOnPhone);
        Assert.Empty(_store.All);
    }

    private sealed class MemoryStore : IPairedPhoneStore
    {
        private readonly List<PairedPhone> _phones = [];
        public IReadOnlyList<PairedPhone> All => _phones;
        public bool IsPaired(ReadOnlySpan<byte> fingerprint)
        {
            var hex = Convert.ToHexStringLower(fingerprint);
            return _phones.Any(p => p.FingerprintHex == hex);
        }
        public void Add(PairedPhone phone) { _phones.Add(phone); Changed?.Invoke(); }
        public void Remove(string fingerprintHex) { _phones.RemoveAll(p => p.FingerprintHex == fingerprintHex); Changed?.Invoke(); }
        public event Action? Changed;
    }
}
