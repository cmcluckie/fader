using System.Net;
using Fader.Shared.Net;

namespace Fader.Diagnostics.X32LocateTest;

/// <summary>
/// Tests the X32 address-resolution ordering without a network, by injecting
/// which addresses "answer". Covers the behaviour that matters: last-known wins,
/// a hand-typed seed is used when there is no memory or the memory is dead,
/// discovery is the last resort, and a dead candidate is skipped rather than
/// used blindly.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.WriteLine("X32 locate - resolution ordering");
        Console.WriteLine("================================\n");

        // A prober that only "answers" for addresses in a set.
        static Func<IPAddress, Task<bool>> Answers(params string[] live)
        {
            var set = live.ToHashSet();
            return ip => Task.FromResult(set.Contains(ip.ToString()));
        }

        static Func<Task<IPAddress?>> Discovers(string? found) =>
            () => Task.FromResult(found is null ? null : IPAddress.Parse(found));

        static Func<Task<IPAddress?>> NeverDiscovers() =>
            () => Task.FromResult<IPAddress?>(null);

        // 1. Last-known answers -> used, and discovery is never consulted.
        var discoveryRan = false;
        var r = await X32Locator.ResolveCore(
            lastKnown: "192.168.9.113",
            seed: "10.0.0.5",
            probe: Answers("192.168.9.113", "10.0.0.5"),
            discover: () => { discoveryRan = true; return Task.FromResult<IPAddress?>(null); });
        Check("last-known is used when it answers", r?.ToString() == "192.168.9.113");
        Check("discovery is skipped when last-known answers", !discoveryRan);

        // 2. No memory, seed answers -> seed used.
        r = await X32Locator.ResolveCore(
            lastKnown: null,
            seed: "10.0.0.5",
            probe: Answers("10.0.0.5"),
            discover: NeverDiscovers());
        Check("configured seed is used when there is no memory", r?.ToString() == "10.0.0.5");

        // 3. Last-known is stale (does not answer), seed answers -> seed used.
        r = await X32Locator.ResolveCore(
            lastKnown: "192.168.9.113",
            seed: "10.0.0.5",
            probe: Answers("10.0.0.5"),
            discover: NeverDiscovers());
        Check("a dead last-known falls through to the seed", r?.ToString() == "10.0.0.5");

        // 4. Neither answers -> discovery result used.
        r = await X32Locator.ResolveCore(
            lastKnown: "192.168.9.113",
            seed: "10.0.0.5",
            probe: Answers(/* nothing */),
            discover: Discovers("172.16.0.9"));
        Check("discovery is the last resort", r?.ToString() == "172.16.0.9");

        // 5. Nothing answers anywhere -> null (caller keeps asking / shows setup).
        r = await X32Locator.ResolveCore(
            lastKnown: "192.168.9.113",
            seed: "10.0.0.5",
            probe: Answers(),
            discover: NeverDiscovers());
        Check("returns null when the console is truly absent", r is null);

        // 6. Garbage seed does not throw and is simply skipped.
        r = await X32Locator.ResolveCore(
            lastKnown: null,
            seed: "not-an-ip",
            probe: Answers("not-an-ip"),
            discover: Discovers("172.16.0.9"));
        Check("an unparseable seed is skipped, not crashed on", r?.ToString() == "172.16.0.9");

        // 7. The store round-trips an address through a temp file.
        var tmp = Path.Combine(Path.GetTempPath(), $"x32store-{Guid.NewGuid():N}.json");
        try
        {
            var store = new X32AddressStore(tmp);
            Check("a fresh store has no memory", store.LoadLast() is null);
            store.SaveLast("192.168.9.113");
            Check("the store round-trips the saved address",
                new X32AddressStore(tmp).LoadLast() == "192.168.9.113");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string description, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {description}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {description}"); }
    }
}
