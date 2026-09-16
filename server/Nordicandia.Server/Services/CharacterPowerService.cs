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
        GameStore.Instance.ApplySkillAssignment(Owner, req.CharacterId, req.Skills, PowerSlotTypes.SkillTraining);
        var now = DateTime.UtcNow;
        var trainings = (req.Skills ?? new()).Select(s => new CharacterSkillTrainingEntry
        {
            PowerId = s.PowerId, TrainingStarted = now, TrainingEnds = now.AddMinutes(5), TrainingToLevel = 1,
        }).ToList();
        return UnaryResult.FromResult(new AssignCharacterPassiveSkillTrainingResponse { Success = true, SkillTrainings = trainings });
    }

    // Mastery is derived from allocated mastery points in the character data; the client
    // only needs an acknowledgement so its local tree stays interactive.
    public UnaryResult<LevelUpCharacterSkillMasteryResponse> LevelUpSkillMastery(LevelUpCharacterSkillMasteryRequest req)
        => UnaryResult.FromResult(new LevelUpCharacterSkillMasteryResponse { NewMasteryLevel = 1 });

    public UnaryResult<ResetCharacterSkillMasteryTreeResponse> ResetSkillMastery(ResetCharacterSkillMasteryTreeRequest req)
        => UnaryResult.FromResult(new ResetCharacterSkillMasteryTreeResponse());

    public UnaryResult<ResetAllCharacterSkillMasteryTreesResponse> ResetAllSkillMasteries(ResetAllCharacterSkillMasteryTreesRequest req)
        => UnaryResult.FromResult(new ResetAllCharacterSkillMasteryTreesResponse());
}
