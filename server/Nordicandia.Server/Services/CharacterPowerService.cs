using MagicOnion;
using SharedNet.Api;
using SharedNet.Constants.Game;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed partial class CharacterPowerServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    public UnaryResult<AssignCharacterActiveSkillResponse> AssignActiveSkill(AssignCharacterActiveSkillRequest req)
    {
        GameStore.Instance.ApplySkillAssignment(Owner, req.CharacterId, req.Skills, PowerSlotTypes.ActiveSkill);
        return UnaryResult.FromResult(new AssignCharacterActiveSkillResponse { Success = true });
    }

    public UnaryResult<AssignCharacterPassiveSkillResponse> AssignPassiveSkill(AssignCharacterPassiveSkillRequest req)
    {
        GameStore.Instance.ApplySkillAssignment(Owner, req.CharacterId, req.Skills, PowerSlotTypes.PassiveSkill);
        return UnaryResult.FromResult(new AssignCharacterPassiveSkillResponse { Success = true });
    }

    public UnaryResult<AssignCharacterPassiveSkillTrainingResponse> AssignPassiveSkillTraining(AssignCharacterPassiveSkillTrainingRequest req)
    {
        var trainings = GameStore.Instance.ApplySkillAssignment(Owner, req.CharacterId, req.Skills, PowerSlotTypes.SkillTraining);
        return UnaryResult.FromResult(new AssignCharacterPassiveSkillTrainingResponse { Success = true, SkillTrainings = trainings });
    }

    // Mastery is derived from allocated mastery points in the character data; the client
    // only needs an acknowledgement so its local tree stays interactive.
    public UnaryResult<LevelUpCharacterSkillMasteryResponse> LevelUpSkillMastery(LevelUpCharacterSkillMasteryRequest req)
        => UnaryResult.FromResult(new LevelUpCharacterSkillMasteryResponse { NewMasteryLevel = 1 });

    public UnaryResult<ResetCharacterSkillMasteryTreeResponse> ResetSkillMastery(ResetCharacterSkillMasteryTreeRequest req)
    {
        // C07: deduct OpalCost, reset all mastery ranks for the character.
        // (PowerId-specific reset requires client metadata Guid mapping; we reset all
        // for now and document the simplification.)
        var store = GameStore.Instance;
        var opals = store.GetOpals(Owner, req.CharacterId);
        if (opals < req.OpalCost)
            return UnaryResult.FromResult(new ResetCharacterSkillMasteryTreeResponse { NewOpals = opals });
        var spent = store.MasteryPointsSpent(Owner, req.CharacterId);
        if (req.OpalCost > 0)
            opals = store.AddOpals(Owner, req.CharacterId, -req.OpalCost);
        store.ResetMasteryRanks(Owner, req.CharacterId);
        return UnaryResult.FromResult(new ResetCharacterSkillMasteryTreeResponse
        {
            GainedMasteryPoints = spent,
            NewOpals = opals,
        });
    }

    public UnaryResult<ResetAllCharacterSkillMasteryTreesResponse> ResetAllSkillMasteries(ResetAllCharacterSkillMasteryTreesRequest req)
    {
        // C07: deduct OpalCost, reset all mastery ranks.
        var store = GameStore.Instance;
        var opals = store.GetOpals(Owner, req.CharacterId);
        if (opals < req.OpalCost)
            return UnaryResult.FromResult(new ResetAllCharacterSkillMasteryTreesResponse { NewOpals = opals });
        var spent = store.MasteryPointsSpent(Owner, req.CharacterId);
        if (req.OpalCost > 0)
            opals = store.AddOpals(Owner, req.CharacterId, -req.OpalCost);
        store.ResetMasteryRanks(Owner, req.CharacterId);
        return UnaryResult.FromResult(new ResetAllCharacterSkillMasteryTreesResponse
        {
            GainedMasteryPoints = spent,
            NewOpals = opals,
        });
    }
}
