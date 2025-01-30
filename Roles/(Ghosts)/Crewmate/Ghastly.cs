using AmongUs.GameOptions;
using Hazel;
using InnerNet;
using TOHE.Roles.Core;
using TOHE.Roles.Double;
using UnityEngine;
using static TOHE.Options;
using static TOHE.Translator;
using static TOHE.Utils;

namespace TOHE.Roles._Ghosts_.Crewmate;

internal class Ghastly : RoleBase
{
    //===========================SETUP================================\\
    public override CustomRoles Role => CustomRoles.Ghastly;
    private const int Id = 22060;
    public static bool HasEnabled => CustomRoleManager.HasEnabled(CustomRoles.Ghastly);
    public override CustomRoles ThisRoleBase => CustomRoles.GuardianAngel;
    public override Custom_RoleType ThisRoleType => Custom_RoleType.CrewmateGhosts;
    //==================================================================\\

    private static OptionItem PossessCooldown;
    private static OptionItem MaxPossesions;
    private static OptionItem PossessDur;
    private static OptionItem GhastlySpeed;
    private static OptionItem GhastlyKillAllies;

    private (byte, byte) killertarget = (byte.MaxValue, byte.MaxValue);
    private readonly Dictionary<byte, long> LastTime = [];
    private bool KillerIsChosen = false;

    public override void SetupCustomOption()
    {
        SetupSingleRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Ghastly);
        PossessCooldown = FloatOptionItem.Create(Id + 10, "GhastlyPossessCD", new(2.5f, 120f, 2.5f), 35f, TabGroup.CrewmateRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Ghastly])
            .SetValueFormat(OptionFormat.Seconds);
        MaxPossesions = IntegerOptionItem.Create(Id + 11, "GhastlyMaxPossessions", new(1, 99, 1), 10, TabGroup.CrewmateRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Ghastly])
            .SetValueFormat(OptionFormat.Players);
        PossessDur = IntegerOptionItem.Create(Id + 12, "GhastlyPossessionDuration", new(5, 120, 5), 40, TabGroup.CrewmateRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Ghastly])
            .SetValueFormat(OptionFormat.Seconds);
        GhastlySpeed = FloatOptionItem.Create(Id + 13, "GhastlySpeed", new(1.5f, 5f, 0.5f), 2f, TabGroup.CrewmateRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Ghastly])
            .SetValueFormat(OptionFormat.Multiplier);
        GhastlyKillAllies = BooleanOptionItem.Create(Id + 14, "GhastlyKillAllies", false, TabGroup.CrewmateRoles, false)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Ghastly]);
    }

    public override void Init()
    {
        KillerIsChosen = false;
        killertarget = (byte.MaxValue, byte.MaxValue);
        LastTime.Clear();
    }

    public override void Add(byte playerId)
    {
        AbilityLimit = MaxPossesions.GetInt();

        CustomRoleManager.OnFixedUpdateOthers.Add(OnFixUpdateOthers);
        CustomRoleManager.CheckDeadBodyOthers.Add(CheckDeadBody);
    }

    private void SendRPC()
    {
        var writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.PlayerId, (byte)CustomRPC.SyncRoleSkill, SendOption.Reliable, -1);
        writer.WriteNetObject(_Player);
        writer.Write(AbilityLimit);
        writer.Write(KillerIsChosen);
        writer.Write(killertarget.Item1);
        writer.Write(killertarget.Item2);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    public override void ReceiveRPC(MessageReader reader, PlayerControl pc)
    {
        AbilityLimit = reader.ReadSingle();
        KillerIsChosen = reader.ReadBoolean();
        var item1 = reader.ReadByte();
        var item2 = reader.ReadByte();
        killertarget = (item1, item2);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.GuardianAngelCooldown = PossessCooldown.GetFloat();
        AURoleOptions.ProtectionDurationSeconds = 0f;
    }
    public override bool OnCheckProtect(PlayerControl angel, PlayerControl target)
    {
        if (target.Is(CustomRoles.NiceMini) && Mini.Age < 18)
        {
            angel.Notify(ColorString(GetRoleColor(CustomRoles.Gangster), GetString("CantPosses")));
            return true;
        }

        if (AbilityLimit <= 0)
        {
            SendRPC();
            angel.Notify(GetString("GhastlyNoMorePossess"));
            return false;
        }

        var (killer, Target) = killertarget;

        if (!KillerIsChosen && !CheckConflicts(target))
        {
            angel.Notify(GetString("GhastlyCannotPossessTarget"));
            return false;
        }

        if (!KillerIsChosen && target.PlayerId != killer)
        {
            TargetArrow.Remove(killer, Target);
            LastTime.Remove(killer);
            killer = target.PlayerId;
            Target = byte.MaxValue;
            KillerIsChosen = true;

            angel.Notify($"\n{GetString("GhastlyChooseTarget")}\n");
        }
        else if (KillerIsChosen && Target == byte.MaxValue && target.PlayerId != killer)
        {
            Target = target.PlayerId;
            AbilityLimit--;
            LastTime.Add(killer, GetTimeStamp());

            KillerIsChosen = false;
            GetPlayerById(killer)?.Notify(GetString("GhastlyYouvePosses"));
            angel.Notify($"\n<size=65%>〘{string.Format(GetString("GhastlyPossessedUser"), "</size>" + GetPlayerById(killer).GetRealName())}<size=65%> 〙</size>\n");

            TargetArrow.Add(killer, Target);
            angel.RpcGuardAndKill(target);
            angel.RpcResetAbilityCooldown();

            Logger.Info($" chosen {target.GetRealName()} for : {GetPlayerById(killer).GetRealName()}", "GhastlyTarget");
        }
        else if (target.PlayerId == killer)
        {
            angel.Notify(GetString("GhastlyCannotPossessTarget"));
        }

        killertarget = (killer, Target);
        SendRPC();

        return false;
    }
    private bool CheckConflicts(PlayerControl target) => target != null && (!GhastlyKillAllies.GetBool() || target.GetCountTypes() != _Player.GetCountTypes());

    public override void OnFixedUpdate(PlayerControl player, bool lowLoad, long nowTime)
    {
        if (lowLoad) return;
        var speed = Main.AllPlayerSpeed.GetValueOrDefault(player.PlayerId, 1f);
        if (speed != GhastlySpeed.GetFloat())
        {
            Main.AllPlayerSpeed[player.PlayerId] = GhastlySpeed.GetFloat();
            player.MarkDirtySettings();
        }
    }
    private void OnFixUpdateOthers(PlayerControl player, bool lowLoad, long nowTime)
    {
        if (lowLoad) return;

        var (killerId, targetId) = killertarget;
        if (killerId == player.PlayerId && LastTime.TryGetValue(player.PlayerId, out var lastTime) && lastTime + PossessDur.GetInt() <= nowTime)
        {
            _Player?.Notify(string.Format($"\n{GetString("GhastlyExpired")}\n", player.GetRealName()));
            TargetArrow.Remove(killerId, targetId);
            LastTime.Remove(player.PlayerId);
            KillerIsChosen = false;
            killertarget = (byte.MaxValue, byte.MaxValue);
            SendRPC();
        }
    }
    public override bool CheckMurderOnOthersTarget(PlayerControl killer, PlayerControl target)
    {
        var (KillerId, TragetId) = killertarget;
        if (KillerId == killer.PlayerId && TragetId != byte.MaxValue)
        {
            if (TragetId != target.PlayerId)
            {
                killer.Notify(GetString("GhastlyNotUrTarget"));
                return true;
            }
            else
            {
                _Player?.Notify(string.Format($"\n{GetString("GhastlyExpired")}\n", killer.GetRealName()));
                TargetArrow.Remove(KillerId, TragetId);
                LastTime.Remove(killer.PlayerId);
                KillerIsChosen = false;
                killertarget = (byte.MaxValue, byte.MaxValue);
                SendRPC();
            }
        }
        return false;
    }

    public override string GetLowerTextOthers(PlayerControl seer, PlayerControl seen = null, bool isForMeeting = false, bool isForHud = false)
    {
        if (isForMeeting || (seer != seen && seer.IsAlive())) return string.Empty;

        var (killerId, targetId) = killertarget;

        if (killerId == seen.PlayerId && targetId != byte.MaxValue)
        {
            var arrows = TargetArrow.GetArrows(seen, targetId);
            var tar = targetId.GetPlayer().GetRealName();
            if (tar == null) return string.Empty;

            var colorstring = ColorString(GetRoleColor(CustomRoles.Ghastly), "<alpha=#88>" + tar + arrows);
            return colorstring;
        }
        return string.Empty;
    }
    private void CheckDeadBody(PlayerControl killer, PlayerControl target, bool inMeeting)
    {
        if (inMeeting) return;

        var (killerId, targetId) = killertarget;
        if (target.PlayerId == killerId || target.PlayerId == targetId)
        {
            _Player?.Notify(string.Format($"\n{GetString("GhastlyExpired")}\n", GetPlayerById(killerId)));
            TargetArrow.Remove(killerId, targetId);
            LastTime.Remove(target.PlayerId);
            KillerIsChosen = false;
            killertarget = (byte.MaxValue, byte.MaxValue);
            SendRPC();
        }
    }

    public override string GetProgressText(byte playerId, bool cooms)
        => ColorString(AbilityLimit > 0 ? GetRoleColor(CustomRoles.Ghastly).ShadeColor(0.25f) : Color.gray, $"({AbilityLimit})");
}
