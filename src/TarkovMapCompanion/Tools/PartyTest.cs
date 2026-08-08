using TarkovMapCompanion.Party;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Hosts or joins a session from the console, with no window and no game.
/// </summary>
/// <remarks>
/// The one part of position sharing that cannot be unit tested is the bit most likely to go wrong:
/// whether this particular router will open a port. This answers that in a few seconds, and gives
/// anyone who reports "nobody can join" something concrete to run.
/// </remarks>
public static class PartyTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var joining = args.Length > 1 && args[0].Equals("join", StringComparison.OrdinalIgnoreCase);

        // "local" hosts on loopback and skips the router, so two processes on one machine can be
        // put through the whole protocol without involving the network at all.
        var local = args.Any(a => a.Equals("local", StringComparison.OrdinalIgnoreCase));

        var name = joining
            ? args.Length > 2 ? args[2] : "Joiner"
            : args.FirstOrDefault(a => !a.Equals("local", StringComparison.OrdinalIgnoreCase)) ?? "Host";

        using var session = new PartySession();

        session.Status += (_, message) => Console.WriteLine($"  {message}");
        session.Changed += (_, _) =>
        {
            var peers = session.Peers;
            if (peers.Count == 0)
                return;

            Console.WriteLine($"  roster: {string.Join(", ", peers.Select(Describe))}");
        };

        if (joining)
        {
            Console.WriteLine($"joining as {name}...");

            if (!await session.JoinAsync(args[1], name).ConfigureAwait(false))
                return 1;
        }
        else
        {
            Console.WriteLine($"hosting as {name}...");

            if (!await session.HostAsync(name, CancellationToken.None, useRouter: !local).ConfigureAwait(false))
            {
                Console.WriteLine();
                Console.WriteLine("Hosting failed. Either UPnP is off on the router, or the connection is");
                Console.WriteLine("behind carrier-grade NAT, which has no inbound path at all. Forward a");
                Console.WriteLine("port manually, or have somebody else host.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"  CODE: {session.Code}");
            Console.WriteLine();
            Console.WriteLine("Join it from another machine with:");
            Console.WriteLine($"  TarkovMapCompanion --party-test join {session.Code} Friend");
        }

        Console.WriteLine();
        Console.WriteLine("Publishing a position every 5 seconds. Ctrl+C to stop.");

        var step = 0;

        while (step < 60)
        {
            await Task.Delay(5000).ConfigureAwait(false);

            // A slow walk east, so movement is visible on the other end.
            session.Publish("customs", new Maps.GamePosition(step * 10, 2, -100), (step * 15) % 360);
            step++;
        }

        return 0;
    }

    private static string Describe(PartyPeer peer)
    {
        if (peer.IsSelf)
            return $"{peer.Name} (you)";

        return peer.HasPosition
            ? $"{peer.Name} @ {peer.Position.X:F0},{peer.Position.Z:F0} on {peer.Map} ({peer.AgeSeconds:F0}s)"
            : $"{peer.Name} (no position)";
    }
}
