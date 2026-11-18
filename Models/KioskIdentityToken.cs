using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace C_LabTap.Models;

// Fixes: LogbookController.LookupStudent used to hand the browser the
// student's real database id, and CreateEquipmentBorrow/EquipmentBorrowed
// just trusted whatever id was posted back - no check that the caller
// had actually identified as that student. Since LogbookController is
// [AllowAnonymous] (the public kiosk) and ids are small sequential
// numbers, anyone on the internet could POST a guessed id and borrow
// items - or read what's borrowed - under any real student's name,
// with no RFID tap or LRN at all (see chat: "run a hacking simulation" /
// SEC-1).
//
// Fix: LookupStudent now hands back a random, unguessable token instead
// of the real id, and only that token - not a raw id - is accepted by
// the later Equipment calls. A token is only ever minted right after a
// real identify (RFID tap or LRN lookup) succeeded, so holding one is
// proof the holder actually went through that step. It's kept in memory
// only (not a database table, unlike LoginAttemptGuard's lockouts) -
// it's short-lived on purpose and losing it on an app restart just
// means a student re-identifies, which the kiosk flow already expects
// on every fresh visit anyway.
public static class KioskIdentityToken
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (long StudentId, DateTimeOffset ExpiresAt)> Tokens = new();

    // 192 bits of randomness - far too large to guess within the
    // 10-minute window, unlike the sequential database id it replaces.
    public static string Issue(long studentId)
    {
        PruneExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Tokens[token] = (studentId, DateTimeOffset.UtcNow.Add(Lifetime));
        return token;
    }

    // Null if the token is missing, malformed, or expired - callers
    // should treat that as "identify again", same as a lookup miss.
    public static long? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!Tokens.TryGetValue(token, out var entry)) return null;
        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            Tokens.TryRemove(token, out _);
            return null;
        }
        return entry.StudentId;
    }

    // Lazy cleanup on every issue, rather than a background timer - the
    // kiosk's token volume is tiny (one terminal, a handful of taps a
    // day), so there's nothing to gain from a more complex scheduler.
    private static void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in Tokens)
        {
            if (kvp.Value.ExpiresAt < now)
            {
                Tokens.TryRemove(kvp.Key, out _);
            }
        }
    }
}
