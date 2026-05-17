using Godot;

namespace PratfallModFramework;

public class ModVoteSession
{
    private readonly Dictionary<string, VoteState> _activeVotes = new();

    public event Action<string, bool>? OnVoteResolved;

    public void StartVote(string voteId, ModManifest manifest, int totalPlayers)
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
            VotedPeers = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        };
        GD.Print($"[ModFramework] Vote started for mod: {voteId} ({manifest.Name})");
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
        var totalVotes = vote.VotedPeers.Count;

        if (totalVotes >= vote.ExpectedVotes)
        {
            bool passed = vote.YesVotes > vote.NoVotes;
            GD.Print($"[ModFramework] Vote for {voteId}: {(passed ? "PASSED" : "FAILED")} ({vote.YesVotes}/{vote.NoVotes})");
            OnVoteResolved?.Invoke(voteId, passed);
            _activeVotes.Remove(voteId);
        }
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
        public HashSet<string> VotedPeers = new(System.StringComparer.OrdinalIgnoreCase);
    }
}
