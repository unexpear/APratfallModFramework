using Godot;

namespace PratfallModFramework;

public class ModVoteSession
{
    private readonly Dictionary<string, VoteState> _activeVotes = new();

    public event Action<string, bool>? OnVoteResolved;

    // Binary-compat overload: ModVoteSession + StartVote are public, and mods compiled
    // against the pre-P3 framework call this exact 3-arg method. P3 added the SessionVoteRule
    // parameter; a defaulted param is source-compatible but NOT binary-compatible, so the
    // 3-arg method must continue to exist as its own slot or those mods throw
    // MissingMethodException at load. Forwards to the 4-arg form with the historical default
    // (Majority). NOTE: the 4-arg overload deliberately has NO default — keeping one here
    // would make StartVote(a,b,c) an ambiguous call (CS0121) against this overload.
    public void StartVote(string voteId, ModManifest manifest, int totalPlayers)
        => StartVote(voteId, manifest, totalPlayers, SessionVoteRule.Majority);

    public void StartVote(string voteId, ModManifest manifest, int totalPlayers, SessionVoteRule rule)
    {
        if (_activeVotes.ContainsKey(voteId))
        {
            GD.Print($"[ModFramework] Vote already active for {voteId}");
            return;
        }
        _activeVotes[voteId] = new VoteState
        {
            VoteId = voteId,
            Manifest = manifest,
            YesVotes = 0,
            NoVotes = 0,
            ExpectedVotes = Math.Max(totalPlayers, 1),
            Rule = rule,
            VotedPeers = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        };
        GD.Print($"[ModFramework] Vote started for mod: {voteId} ({manifest.Name}) rule={rule}");
    }

    public void CastVote(string voteId, string voterId, bool voteYes)
    {
        if (!_activeVotes.TryGetValue(voteId, out var vote)) return;
        if (string.IsNullOrWhiteSpace(voterId) || vote.VotedPeers.Contains(voterId)) return;

        vote.VotedPeers.Add(voterId);
        if (voteYes) vote.YesVotes++;
        else vote.NoVotes++;

        CheckVoteResult(voteId);
    }

    private void CheckVoteResult(string voteId)
    {
        var vote = _activeVotes[voteId];

        // Unanimous early-fail: any No -> fail immediately, no need to wait for the rest.
        if (vote.Rule == SessionVoteRule.Unanimous && vote.NoVotes > 0)
        {
            Resolve(voteId, vote, passed: false);
            return;
        }

        var totalVotes = vote.VotedPeers.Count;
        if (totalVotes >= vote.ExpectedVotes)
        {
            bool passed = vote.Rule == SessionVoteRule.Unanimous
                ? vote.NoVotes == 0 && vote.YesVotes == vote.ExpectedVotes
                : vote.YesVotes > vote.NoVotes;
            Resolve(voteId, vote, passed);
        }
    }

    private void Resolve(string voteId, VoteState vote, bool passed)
    {
        GD.Print($"[ModFramework] Vote for {voteId}: {(passed ? "PASSED" : "FAILED")} ({vote.YesVotes}/{vote.NoVotes}, rule={vote.Rule})");
        OnVoteResolved?.Invoke(voteId, passed);
        _activeVotes.Remove(voteId);
    }

    public void ClearAllVotes()
    {
        _activeVotes.Clear();
    }

    private sealed class VoteState
    {
        public string VoteId = "";
        public ModManifest Manifest = new();
        public int YesVotes;
        public int NoVotes;
        public int ExpectedVotes;
        public SessionVoteRule Rule = SessionVoteRule.Majority;
        public HashSet<string> VotedPeers = new(System.StringComparer.OrdinalIgnoreCase);
    }
}
