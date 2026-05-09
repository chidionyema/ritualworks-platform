using System.Security.Cryptography;
using System.Text;
using Haworks.BuildingBlocks.Vault;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Infrastructure.Authentication;

/// <summary>
/// Background-polling RSA signing key ring backed by Vault KV.
///
/// <para>
/// On startup, reads <c>secret/identity/jwt</c> field <c>signing_key</c>
/// (RSA private key in PEM form). If the field is absent, generates a
/// fresh RSA-2048 keypair and persists it back so subsequent restarts pick
/// it up. The key becomes the ring's first <see cref="JwtSigningKeyStatus.Active"/>
/// entry.
/// </para>
///
/// <para>
/// As an <see cref="IHostedService"/>, polls Vault every
/// <see cref="PollInterval"/>. When the PEM at the Vault path differs from
/// the current Active entry's source PEM, the new key is added as Active and
/// the previous Active is demoted to <see cref="JwtSigningKeyStatus.Retiring"/>
/// with <c>RetiredAt = now + 1h</c>. The same loop drops any retiring entry
/// whose grace window has elapsed.
/// </para>
///
/// <para>
/// All ring mutations are guarded by a <see cref="ReaderWriterLockSlim"/>;
/// <see cref="AllValidKeys"/> returns an immutable snapshot so JWKS
/// publication never observes a half-mutated ring.
/// </para>
/// </summary>
public sealed class RotatingJwtSigningKeyRing : IHostedService, IJwtSigningKeyRing, IDisposable
{
    /// <summary>Vault KV mount + path that holds the signing key PEM.</summary>
    public const string VaultKvMount = "secret";
    public const string VaultKvPath = "identity/jwt";
    public const string VaultKvField = "signing_key";

    /// <summary>Grace window kept around a key after it has been rotated out.</summary>
    public static readonly TimeSpan RetirementGrace = TimeSpan.FromHours(1);

    /// <summary>Background poll interval.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IVaultService _vault;
    private readonly ILogger<RotatingJwtSigningKeyRing> _logger;
    private readonly TimeProvider _time;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<JwtSigningKeyEntry> _entries = new();

    // Tracks the PEM string the current Active entry was loaded from so we
    // can detect rotation by string compare without re-deriving the kid.
    private string? _activePem;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public RotatingJwtSigningKeyRing(
        IVaultService vault,
        ILogger<RotatingJwtSigningKeyRing> logger,
        TimeProvider? time = null)
    {
        _vault = vault;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public JwtSigningKeyEntry Active
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                var active = _entries.FirstOrDefault(e => e.Status == JwtSigningKeyStatus.Active)
                    ?? throw new InvalidOperationException(
                        "JWT signing key ring has no Active key. Did StartAsync run?");
                return active;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public IReadOnlyList<JwtSigningKeyEntry> AllValidKeys
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                // Defensive copy — caller gets an immutable snapshot.
                return _entries.ToArray();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Vault must be initialized for KV reads to succeed; the existing
        // singleton VaultService is initialized lazily, so call it here so the
        // first poll doesn't race the first request.
        await _vault.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Initial load — if Vault has no signing_key yet (e.g. fresh dev env),
        // generate one and persist it via the existing per-secret bootstrap.
        await SeedFromVaultAsync(cancellationToken).ConfigureAwait(false);

        // Kick off the background poll loop. Don't await — IHostedService
        // contract is "start the work then return".
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => PollLoopAsync(_loopCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is null) return;

        try
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed — nothing to do.
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    private async Task SeedFromVaultAsync(CancellationToken ct)
    {
        var pem = await _vault.GetKvSecretAsync(VaultKvPath, VaultKvField, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(pem))
        {
            // No signing key in Vault yet — generate one. We deliberately do
            // NOT write it back here: the existing operational seed flow
            // (vault-init / kv-dev-values.json) is the source of truth, and
            // generating-without-persisting keeps this hosted service
            // side-effect-free against Vault. Restart will produce a new key
            // and rotate cleanly via the same code path.
            _logger.LogWarning(
                "No signing key found at {Mount}/{Path}#{Field}; generating an ephemeral RSA-2048 key for this process.",
                VaultKvMount, VaultKvPath, VaultKvField);

            using var rsa = RSA.Create(2048);
            pem = rsa.ExportRSAPrivateKeyPem();
        }

        JwtSigningKeyEntry entry;
        try
        {
            entry = BuildEntry(pem!, JwtSigningKeyStatus.Active, _time.GetUtcNow(), retiredAt: null);
        }
        catch (Exception ex)
        {
            // Vault has SOMETHING at this path but it isn't a parseable PEM
            // RSA key. Most likely cause: seed.sh writes a placeholder string
            // ("dev-only-not-for-prod") that hasn't been replaced by a real
            // operator-supplied key yet. Fall back to an ephemeral key so
            // identity boots — same behavior as "no key set". Operators see
            // the warning + can `vault kv put` a real PEM at any time.
            _logger.LogWarning(ex,
                "Vault key at {Mount}/{Path}#{Field} is not a parseable PEM RSA private key; generating an ephemeral key. Replace with `vault kv put` of a real RSA PEM to start using a persistent key.",
                VaultKvMount, VaultKvPath, VaultKvField);

            using var rsa = RSA.Create(2048);
            pem = rsa.ExportRSAPrivateKeyPem();
            entry = BuildEntry(pem, JwtSigningKeyStatus.Active, _time.GetUtcNow(), retiredAt: null);
        }

        _lock.EnterWriteLock();
        try
        {
            _entries.Clear();
            _entries.Add(entry);
            _activePem = pem;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _logger.LogInformation(
            "JWT signing key ring seeded. Active kid={Kid}", entry.Kid);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Don't let poll errors kill the loop; Vault may be briefly
                // unreachable. Log and try again on the next tick.
                _logger.LogError(ex, "JWT signing key ring poll iteration failed; will retry.");
            }
        }
    }

    /// <summary>Single poll iteration — exposed internal for testability.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var latestPem = await _vault.GetKvSecretAsync(VaultKvPath, VaultKvField, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        _lock.EnterUpgradeableReadLock();
        try
        {
            // Step 1: detect rotation (Vault PEM differs from current Active).
            var rotated = !string.IsNullOrWhiteSpace(latestPem)
                       && !string.Equals(latestPem, _activePem, StringComparison.Ordinal);

            // Step 2: are there any expired retiring keys to drop?
            var hasExpiredRetirees = _entries.Any(e =>
                e.Status == JwtSigningKeyStatus.Retiring
                && e.RetiredAt is { } retiredAt
                && retiredAt < now);

            if (!rotated && !hasExpiredRetirees) return;

            _lock.EnterWriteLock();
            try
            {
                if (rotated)
                {
                    JwtSigningKeyEntry newEntry;
                    try
                    {
                        newEntry = BuildEntry(latestPem!, JwtSigningKeyStatus.Active, now, retiredAt: null);
                    }
                    catch (Exception ex)
                    {
                        // Vault has a new value but it's not parseable PEM. Don't
                        // crash the poller — keep the current ring, log the bad
                        // input, and try again next tick (operator may fix it).
                        _logger.LogWarning(ex,
                            "Vault key at {Mount}/{Path}#{Field} changed but is not a parseable PEM RSA private key; keeping current ring.",
                            VaultKvMount, VaultKvPath, VaultKvField);
                        return;
                    }

                    // Demote whatever was Active to Retiring with a 1h grace.
                    for (var i = 0; i < _entries.Count; i++)
                    {
                        if (_entries[i].Status == JwtSigningKeyStatus.Active)
                        {
                            var demoted = _entries[i] with
                            {
                                Status = JwtSigningKeyStatus.Retiring,
                                RetiredAt = now + RetirementGrace,
                            };
                            _entries[i] = demoted;
                            _logger.LogInformation(
                                "JWT signing key {Kid} demoted to Retiring; grace expires {RetiredAt:O}.",
                                demoted.Kid, demoted.RetiredAt);
                        }
                    }

                    _entries.Add(newEntry);
                    _activePem = latestPem;
                    _logger.LogInformation(
                        "JWT signing key rotated. New Active kid={Kid}.", newEntry.Kid);
                }

                if (hasExpiredRetirees)
                {
                    var dropped = _entries.RemoveAll(e =>
                        e.Status == JwtSigningKeyStatus.Retiring
                        && e.RetiredAt is { } retiredAt
                        && retiredAt < now);
                    if (dropped > 0)
                    {
                        _logger.LogInformation(
                            "Dropped {Count} expired retiring JWT signing key(s) from the ring.", dropped);
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    private static JwtSigningKeyEntry BuildEntry(
        string pem,
        JwtSigningKeyStatus status,
        DateTimeOffset addedAt,
        DateTimeOffset? retiredAt)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        var kid = ComputeKid(rsa);
        var key = new RsaSecurityKey(rsa) { KeyId = kid };

        return new JwtSigningKeyEntry
        {
            Key = key,
            Kid = kid,
            Status = status,
            AddedAt = addedAt,
            RetiredAt = retiredAt,
        };
    }

    /// <summary>
    /// Stable Kid derivation: SHA-256 over the SubjectPublicKeyInfo DER
    /// encoding, base64url without padding. Two RSA private keys with the
    /// same public modulus + exponent produce the same Kid, so persisting the
    /// PEM to Vault and reading it back yields a deterministic identifier.
    /// </summary>
    private static string ComputeKid(RSA rsa)
    {
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Base64UrlEncoder.Encode(hash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loopCts?.Dispose();
        _lock.Dispose();
    }
}
