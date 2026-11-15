using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyAiAssistant.Services.Pairing;

/// <summary>
/// Short-lived pairing invites.
///
/// An invite is the user's decision to let one more device in, so it is deliberately narrow: one
/// group, one code, one successful use, a short lifetime and a small number of attempts. Nothing
/// here is persisted — an invite that survives a restart would outlive the moment the user
/// intended it for, and the cost of re-creating one is a single click.
/// </summary>
public sealed class PairingInviteStore
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    private const int DefaultMaxAttempts = 5;
    private const string InviteIdPrefix = "INV";

    /// <summary>
    /// How long a dead invite is remembered so the user can be told why it failed. It is never
    /// accepted past its expiry; this only keeps the reason available.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, InviteState> _invites = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public PairingInviteStore(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public PairingInvite Create(string groupId, TimeSpan? lifetime = null, int maxAttempts = DefaultMaxAttempts)
    {
        if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("设备组不能为空", nameof(groupId));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var window = lifetime ?? DefaultLifetime;
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));

        var invite = new PairingInvite(
            InviteIdPrefix + Base32.Encode(RandomNumberGenerator.GetBytes(8)),
            groupId,
            NewCode(),
            _clock().Add(window),
            maxAttempts);

        lock (_gate)
        {
            Prune();
            _invites[invite.InviteId] = new InviteState(invite);
        }

        return invite;
    }

    /// <summary>Checks a code against an invite, counting the attempt and burning the invite on success.</summary>
    public PairingOutcome Verify(string inviteId, string code)
    {
        lock (_gate)
        {
            Prune();

            if (string.IsNullOrWhiteSpace(inviteId) || !_invites.TryGetValue(inviteId, out var state))
                return PairingOutcome.Refuse(PairingErrors.UnknownInvite, "邀请不存在或已经失效");

            if (state.Consumed)
                return PairingOutcome.Refuse(PairingErrors.InviteConsumed, "这个邀请已经用过了，请重新生成");

            if (_clock() >= state.Invite.ExpiresAt)
                return PairingOutcome.Refuse(PairingErrors.InviteExpired, "邀请已过期，请重新生成");

            if (state.Attempts >= state.Invite.MaxAttempts)
                return PairingOutcome.Refuse(PairingErrors.TooManyAttempts, "配对码错误次数过多，邀请已作废");

            state.Attempts++;

            if (!Matches(state.Invite.Code, code))
            {
                var left = state.Invite.MaxAttempts - state.Attempts;
                if (left <= 0) return PairingOutcome.Refuse(PairingErrors.TooManyAttempts, "配对码错误次数过多，邀请已作废");

                return PairingOutcome.Refuse(PairingErrors.CodeMismatch, $"配对码不正确，还可以再试 {left} 次");
            }

            state.Consumed = true;
            return new PairingOutcome(true, "配对码正确", GroupId: state.Invite.GroupId, Invite: state.Invite);
        }
    }

    /// <summary>Invites still usable. The code is never included — it is shown once, at creation.</summary>
    public IReadOnlyList<PairingInviteInfo> Active()
    {
        lock (_gate)
        {
            Prune();
            var now = _clock();
            return _invites.Values
                .Where(state => state.IsUsable && now < state.Invite.ExpiresAt)
                .Select(state => new PairingInviteInfo(state.Invite.InviteId, state.Invite.GroupId,
                    state.Invite.ExpiresAt, Math.Max(0, state.Invite.MaxAttempts - state.Attempts)))
                .ToArray();
        }
    }

    public bool Cancel(string inviteId)
    {
        lock (_gate)
        {
            return !string.IsNullOrWhiteSpace(inviteId) && _invites.Remove(inviteId);
        }
    }

    /// <summary>
    /// Drops invites whose expiry is long past. A recently used or burned invite is kept so the
    /// user can be told "this invite was already used" instead of the vaguer "no such invite".
    /// </summary>
    private void Prune()
    {
        var now = _clock();
        foreach (var id in _invites.Where(pair => now >= pair.Value.Invite.ExpiresAt + Retention)
                     .Select(pair => pair.Key).ToArray())
        {
            _invites.Remove(id);
        }
    }

    /// <summary>
    /// Compares in constant time, and compares the same amount of work whatever the length, so the
    /// timing of a wrong code does not say how much of it was right.
    /// </summary>
    private static bool Matches(string expected, string? actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual ?? string.Empty);

        var lengthMatches = expectedBytes.Length == actualBytes.Length;
        if (!lengthMatches)
        {
            // Fold the difference into a comparison anyway, so a short guess is not measurably faster.
            var padded = new byte[expectedBytes.Length];
            actualBytes.AsSpan(0, Math.Min(actualBytes.Length, padded.Length)).CopyTo(padded);
            CryptographicOperations.FixedTimeEquals(expectedBytes, padded);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    /// <summary>Six digits, uniform: values that would make the last million range likelier are discarded.</summary>
    private static string NewCode()
    {
        Span<byte> buffer = stackalloc byte[4];
        const uint limit = uint.MaxValue - uint.MaxValue % 1_000_000;

        while (true)
        {
            RandomNumberGenerator.Fill(buffer);
            var raw = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (raw >= limit) continue;

            return (raw % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
    }

    private sealed class InviteState
    {
        public InviteState(PairingInvite invite) => Invite = invite;

        public PairingInvite Invite { get; }

        public int Attempts { get; set; }

        public bool Consumed { get; set; }

        public bool IsUsable => !Consumed && Attempts < Invite.MaxAttempts;
    }
}
