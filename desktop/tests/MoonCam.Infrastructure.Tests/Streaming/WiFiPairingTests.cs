using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Security;
using MoonCam.Infrastructure.Streaming;

namespace MoonCam.Infrastructure.Tests.Streaming;

/// <summary>
/// Wi-Fi over real TLS 1.3 with real self-signed certificates, playing the phone by hand: unpaired phones are
/// refused, pairing shows the same code on both sides only after both confirm, and a paired phone reconnects
/// without a code.
/// </summary>
public sealed class WiFiPairingTests : IAsyncDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("mooncam-wifi-").FullName;
    private readonly X509Certificate2 _phoneCertificate = PhoneCertificate();
    private readonly StreamController _controller;

    public WiFiPairingTests()
    {
        var pairing = new PairingCoordinator(new JsonPairedPhoneStore(_config), TimeProvider.System);
        _controller = new StreamController(TimeProvider.System, () => new NullDecoder(), port: 0,
            wifi: new WiFiOptions(DesktopIdentity.LoadOrCreate(_config), pairing, Port: 0, Advertise: false));
        _controller.Start();
    }

    [Fact]
    public async Task An_unpaired_phone_is_refused()
    {
        await using var phone = await WiFiPhone.ConnectAsync(_controller.WiFiPort!.Value, _phoneCertificate);

        var welcome = await phone.HelloAsync(pairing: false);

        Assert.False(welcome.Accepted);
        Assert.Contains("not paired", welcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_is_refused_while_the_window_is_closed()
    {
        await using var phone = await WiFiPhone.ConnectAsync(_controller.WiFiPort!.Value, _phoneCertificate);

        var welcome = await phone.HelloAsync(pairing: true);

        Assert.False(welcome.Accepted);
        Assert.Contains("closed", welcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_shows_the_same_code_on_both_sides_and_a_paired_phone_reconnects_without_one()
    {
        var pairing = _controller.Pairing!;
        pairing.OpenWindow();

        await using (var phone = await WiFiPhone.ConnectAsync(_controller.WiFiPort!.Value, _phoneCertificate))
        {
            var welcome = await phone.HelloAsync(pairing: true);
            Assert.True(welcome.Accepted);
            Assert.True(welcome.Pairing);

            var phoneCode = await phone.RunPairingAsync();
            var desktopCode = await WaitAsync(() => pairing.Pending?.Code);
            Assert.Equal(desktopCode, phoneCode); // the human compares these two numbers

            await phone.SendAsync(new ControlMessage(ControlOps.PairConfirm));
            await WaitAsync(() => pairing.Pending is null ? null : (pairing.ApprovePending() ? "ok" : null));

            var paired = Messages.ReadControl(await phone.ReadUntilAsync(FrameType.Control));
            Assert.Equal(ControlOps.Paired, paired.Op);
            Assert.True(pairing.Store.IsPaired(Pairing.Fingerprint(_phoneCertificate.RawData)));
        }

        await WaitAsync(() => _controller.Phone is null ? "gone" : null);
        await using var again = await WiFiPhone.ConnectAsync(_controller.WiFiPort!.Value, _phoneCertificate);
        var second = await again.HelloAsync(pairing: false);
        Assert.True(second.Accepted);
        Assert.Null(second.Pairing);
    }

    [Fact]
    public async Task A_phone_that_leaves_mid_pairing_frees_the_slot_for_the_next_attempt()
    {
        var pairing = _controller.Pairing!;
        pairing.OpenWindow();
        await using (var phone = await WiFiPhone.ConnectAsync(_controller.WiFiPort!.Value, _phoneCertificate))
        {
            Assert.True((await phone.HelloAsync(pairing: true)).Accepted);
            await phone.RunPairingAsync();
            await WaitAsync(() => pairing.Pending?.Code);
        } // the person pressed Cancel on the phone

        await WaitAsync(() => pairing.Pending is null ? "freed" : null);
        await using var again = await WiFiPhone.ConnectAsync(_controller.WiFiPort!.Value, _phoneCertificate);
        Assert.True((await again.HelloAsync(pairing: true)).Accepted);
    }

    [Fact]
    public async Task Unpairing_on_the_computer_tells_the_connected_phone_and_disconnects_it()
    {
        await using var phone = await PairAsync();
        var fingerprint = Convert.ToHexStringLower(Pairing.Fingerprint(_phoneCertificate.RawData));

        await _controller.UnpairAsync(fingerprint);

        Assert.Equal(ControlOps.Unpair, Messages.ReadControl(await phone.ReadUntilAsync(FrameType.Control)).Op);
        Assert.Equal("unpaired", Messages.ReadBye(await phone.ReadUntilAsync(FrameType.Bye)).Reason);
        Assert.Empty(_controller.Pairing!.Store.All);
    }

    [Fact]
    public async Task A_phone_that_unpairs_itself_is_forgotten_by_the_computer()
    {
        await using var phone = await PairAsync();

        await phone.SendAsync(new ControlMessage(ControlOps.Unpair));

        Assert.Equal("unpaired", Messages.ReadBye(await phone.ReadUntilAsync(FrameType.Bye)).Reason);
        await WaitAsync(() => _controller.Pairing!.Store.All.Count == 0 ? "forgotten" : null);
    }

    /// <summary>A phone that has just paired and stays connected.</summary>
    private async Task<WiFiPhone> PairAsync()
    {
        var pairing = _controller.Pairing!;
        pairing.OpenWindow();
        var phone = await WiFiPhone.ConnectAsync(_controller.WiFiPort!.Value, _phoneCertificate);
        Assert.True((await phone.HelloAsync(pairing: true)).Accepted);
        await phone.RunPairingAsync();
        await WaitAsync(() => pairing.Pending?.Code);
        await phone.SendAsync(new ControlMessage(ControlOps.PairConfirm));
        await WaitAsync(() => pairing.Pending is null ? null : (pairing.ApprovePending() ? "ok" : null));
        Assert.Equal(ControlOps.Paired, Messages.ReadControl(await phone.ReadUntilAsync(FrameType.Control)).Op);
        return phone;
    }

    private static async Task<string> WaitAsync(Func<string?> probe)
    {
        for (var i = 0; i < 200; i++)
        {
            if (probe() is { } value)
                return value;
            await Task.Delay(25);
        }
        throw new TimeoutException("The desktop did not reach the expected pairing state.");
    }

    private static X509Certificate2 PhoneCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Test Phone", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
        Directory.Delete(_config, recursive: true);
    }

    /// <summary>The phone's side of the protocol, as the Android app does it.</summary>
    private sealed class WiFiPhone : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly SslStream _ssl;
        private readonly FrameReader _reader;
        private readonly FrameWriter _writer;
        private readonly byte[] _phoneFingerprint;
        private byte[]? _desktopFingerprint;

        private WiFiPhone(TcpClient tcp, X509Certificate2 certificate)
        {
            _tcp = tcp;
            _phoneFingerprint = Pairing.Fingerprint(certificate.RawData);
            _ssl = new SslStream(tcp.GetStream(), false, (_, server, _, _) =>
            {
                _desktopFingerprint = Pairing.Fingerprint(server!.GetRawCertData()); // pinned after pairing
                return true;
            });
            _reader = new FrameReader(_ssl);
            _writer = new FrameWriter(_ssl);
        }

        public static async Task<WiFiPhone> ConnectAsync(int port, X509Certificate2 certificate)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var phone = new WiFiPhone(tcp, certificate);
            await phone._ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "mooncam",
                ClientCertificates = [certificate],
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            return phone;
        }

        public async Task<WelcomeMessage> HelloAsync(bool pairing)
        {
            await _writer.WritePreambleAsync();
            await _reader.ReadPreambleAsync();
            await _writer.WriteAsync(Messages.Hello(new HelloMessage("Test Phone", "Moon", "Test", "16", "0.2.0", "", pairing ? true : null)));
            return Messages.ReadWelcome((await _reader.ReadAsync())!);
        }

        /// <summary>commit → nonce → reveal, then computes the code the phone would display.</summary>
        public async Task<string> RunPairingAsync()
        {
            var nonce = Pairing.NewNonce();
            await SendAsync(new ControlMessage(ControlOps.PairCommit, Data: Convert.ToBase64String(Pairing.Commit(nonce))));
            var reply = Messages.ReadControl(await ReadUntilAsync(FrameType.Control));
            Assert.Equal(ControlOps.PairNonce, reply.Op);
            await SendAsync(new ControlMessage(ControlOps.PairReveal, Data: Convert.ToBase64String(nonce)));
            return Pairing.Code(_phoneFingerprint, _desktopFingerprint!, nonce, Convert.FromBase64String(reply.Data!));
        }

        public ValueTask SendAsync(ControlMessage message) => _writer.WriteAsync(Messages.Control(message));

        /// <summary>Answers pings like the app does and returns the first frame of the wanted type.</summary>
        public async Task<Frame> ReadUntilAsync(FrameType type)
        {
            while (await _reader.ReadAsync() is { } frame)
            {
                if (frame.Type == type)
                    return frame;
                if (frame.Type == FrameType.Ping)
                    await _writer.WriteAsync(Heartbeat.Pong(frame));
            }
            throw new EndOfStreamException();
        }

        public async ValueTask DisposeAsync()
        {
            _writer.Dispose();
            await _ssl.DisposeAsync();
            _tcp.Dispose();
        }
    }

    private sealed class NullDecoder : IVideoDecoder
    {
        public void Configure(VideoConfig config) { }
        public IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs) => [];
        public void Dispose() { }
    }
}
