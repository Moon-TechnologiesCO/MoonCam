using MoonCam.Core.Protocol;

namespace MoonCam.Core.Session;

/// <summary>What the desktop shows while a phone is pairing.</summary>
public sealed record PairingRequest(string PhoneName, string Code);

/// <summary>
/// Decides when phones may pair and remembers them. Unpaired phones are accepted only while the user has the
/// pairing window open (two minutes), one at a time, and at most <c>maxAttempts</c> per window, so the six-digit
/// code cannot be brute-forced.
/// </summary>
public sealed class PairingCoordinator(IPairedPhoneStore store, TimeProvider time, TimeSpan? window = null, int maxAttempts = 5)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(2);
    private readonly Lock _lock = new();
    private long _openedAt = -1;
    private int _attempts;
    private PairingExchange? _current;

    public IPairedPhoneStore Store => store;

    /// <summary>Raised when the window opens or closes, or a code becomes ready or goes away.</summary>
    public event Action? Changed;

    public bool IsWindowOpen
    {
        get
        {
            lock (_lock)
                return _openedAt >= 0 && time.GetElapsedTime(_openedAt) < _window;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
                return _openedAt < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, (_window - time.GetElapsedTime(_openedAt)).Ticks));
        }
    }

    /// <summary>The code waiting for the user's approval, if any.</summary>
    public PairingRequest? Pending
    {
        get
        {
            lock (_lock)
                return _current is { Code: { } code } exchange && !exchange.IsFinished ? new PairingRequest(exchange.PhoneName, code) : null;
        }
    }

    public void OpenWindow()
    {
        lock (_lock)
        {
            _openedAt = time.GetTimestamp();
            _attempts = 0;
        }
        Changed?.Invoke();
    }

    public void CloseWindow()
    {
        PairingExchange? unfinished;
        lock (_lock)
        {
            _openedAt = -1;
            unfinished = _current is { IsFinished: false } ? _current : null;
            _current = null;
        }
        unfinished?.Fail(); // only an exchange still in progress is cancelled; a completed one stays completed
        Changed?.Invoke();
    }

    /// <summary>Starts pairing a phone, or returns null with the reason it is not allowed now.</summary>
    public PairingExchange? TryBegin(string phoneName, byte[] phoneFingerprint, byte[] desktopFingerprint, out string? refusal)
    {
        lock (_lock)
        {
            refusal = null;
            if (!(_openedAt >= 0 && time.GetElapsedTime(_openedAt) < _window))
                refusal = "Pairing is closed. On the computer, click \"Pair over Wi-Fi\" and try again.";
            else if (_current is { IsFinished: false })
                refusal = "Another phone is pairing right now.";
            else if (++_attempts > maxAttempts)
                refusal = "Too many pairing attempts. Open pairing again on the computer.";
            if (refusal is not null)
                return null;

            var exchange = new PairingExchange(phoneName, phoneFingerprint, desktopFingerprint);
            exchange.StateChanged += OnExchangeChanged;
            _current = exchange;
            return exchange;
        }
    }

    /// <summary>The user confirmed on the computer that the codes match.</summary>
    public bool ApprovePending() => _current?.ApproveOnDesktop() ?? false;

    public void RejectPending()
    {
        _current?.Fail();
        Changed?.Invoke();
    }

    private void OnExchangeChanged(PairingExchange exchange)
    {
        if (exchange.IsComplete)
        {
            store.Add(new PairedPhone(exchange.PhoneName, Convert.ToHexStringLower(exchange.PhoneFingerprint), time.GetUtcNow()));
            CloseWindow();
            return;
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// One pairing run between the desktop and a phone: commit → desktop nonce → reveal → code on both screens →
/// both users confirm. Any deviation (bad reveal, wrong order) fails the exchange for good.
/// </summary>
public sealed class PairingExchange(string phoneName, byte[] phoneFingerprint, byte[] desktopFingerprint)
{
    private readonly Lock _lock = new();
    private byte[]? _commit;
    private byte[]? _desktopNonce;
    private bool _phoneConfirmed;
    private bool _desktopApproved;

    public string PhoneName { get; } = phoneName;
    public byte[] PhoneFingerprint { get; } = phoneFingerprint;
    public string? Code { get; private set; }
    public bool IsComplete { get; private set; }
    public bool IsFailed { get; private set; }
    public bool IsFinished => IsComplete || IsFailed;

    public event Action<PairingExchange>? StateChanged;

    /// <summary>Phone's commitment received; returns the desktop nonce to send back.</summary>
    public byte[] OnCommit(byte[] commit)
    {
        lock (_lock)
        {
            if (IsFinished || _commit is not null || commit.Length != 32)
                throw Fail("Unexpected pairing commitment.");
            _commit = commit;
            _desktopNonce = Pairing.NewNonce();
            return _desktopNonce;
        }
    }

    /// <summary>Phone revealed its nonce: verify it against the commitment and compute the code.</summary>
    public string OnReveal(byte[] phoneNonce)
    {
        lock (_lock)
        {
            if (IsFinished || _commit is null || _desktopNonce is null || Code is not null || !Pairing.VerifyCommit(_commit, phoneNonce))
                throw Fail("The phone's pairing reveal does not match its commitment.");
            Code = Pairing.Code(PhoneFingerprint, desktopFingerprint, phoneNonce, _desktopNonce);
        }
        StateChanged?.Invoke(this);
        return Code;
    }

    public void ConfirmOnPhone()
    {
        lock (_lock)
        {
            if (Code is null || IsFinished)
                throw Fail("Pairing confirmation arrived before the code.");
            _phoneConfirmed = true;
            IsComplete = _desktopApproved;
        }
        StateChanged?.Invoke(this);
    }

    public bool ApproveOnDesktop()
    {
        lock (_lock)
        {
            if (Code is null || IsFinished)
                return false;
            _desktopApproved = true;
            IsComplete = _phoneConfirmed;
        }
        StateChanged?.Invoke(this);
        return true;
    }

    public ProtocolException Fail(string reason = "Pairing cancelled.")
    {
        lock (_lock)
            IsFailed = !IsComplete;
        StateChanged?.Invoke(this);
        return new ProtocolException(reason);
    }
}
