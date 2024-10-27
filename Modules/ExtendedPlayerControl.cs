using AmongUs.GameOptions;
using Hazel;
using InnerNet;
using System;
using System.Text;
using TOHE.Modules;
using TOHE.Patches;
using TOHE.Roles.AddOns.Common;
using TOHE.Roles.AddOns.Impostor;
using TOHE.Roles.Core;
using TOHE.Roles.Crewmate;
using TOHE.Roles.Impostor;
using TOHE.Roles.Neutral;
using UnityEngine;
using static TOHE.Translator;

namespace TOHE;

static class ExtendedPlayerControl
{
    public static void RpcSetCustomRole(this PlayerControl player, CustomRoles role)
    {
        if (role < CustomRoles.NotAssigned)
        {
            Main.PlayerStates[player.PlayerId].SetMainRole(role);
        }
        else if (role >= CustomRoles.NotAssigned)   //500:NoSubRole 501~:SubRole 
        {
            if (Cleanser.CantGetAddon() && player.Is(CustomRoles.Cleansed)) return;
            if (role == CustomRoles.Cleansed) Main.PlayerStates[player.PlayerId].SetSubRole(role, pc: player);
            else Main.PlayerStates[player.PlayerId].SetSubRole(role);
        }
        if (AmongUsClient.Instance.AmHost)
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetCustomRole, SendOption.Reliable, -1);
            writer.Write(player.PlayerId);
            writer.WritePacked((int)role);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
    }
    public static void RpcSetCustomRole(byte PlayerId, CustomRoles role)
    {
        if (AmongUsClient.Instance.AmHost)
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetCustomRole, SendOption.Reliable, -1);
            writer.Write(PlayerId);
            writer.WritePacked((int)role);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
    }
    public static void SetRole(this PlayerControl player, RoleTypes role, bool canOverride)
    {
        player.StartCoroutine(player.CoSetRole(role, canOverride));
    }
    public static void RpcSetRoleDesync(this PlayerControl player, RoleTypes role,/* bool canOverride,*/ int clientId)
    {
        if (player == null) return;
        if (AmongUsClient.Instance.ClientId == clientId)
        {
            player.SetRole(role, true);
            return;
        }
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.SetRole, SendOption.Reliable, clientId);
        writer.Write((ushort)role);
        writer.Write(true);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    public static (RoleTypes RoleType, CustomRoles CustomRole) GetRoleMap(this PlayerControl player, byte targetId = byte.MaxValue) => Utils.GetRoleMap(player.PlayerId, targetId);

    /// <summary>
    /// Revives the player if the given roletype is alive and player is dead.
    /// </summary>
    public static void RpcRevive(this PlayerControl player)
    {
        if (player == null) return;
        if (!player.Data.IsDead && player.IsAlive())
        {
            Logger.Warn($"Invalid Revive for {player.GetRealName()} / player have data is dead: {player.Data.IsDead}, in game states is dead: {!player.IsAlive()}", "RpcRevive");
            return;
        }

        if (player.HasGhostRole())
        {
            player.GetRoleClass().OnRemove(player.PlayerId);
            player.RpcSetCustomRole(player.GetRoleMap().CustomRole);
            player.GetRoleClass().OnAdd(player.PlayerId);
        }

        if (Camouflage.IsCamouflage)
            Camouflage.RpcSetSkin(player);

        var customRole = player.GetCustomRole();
        Main.PlayerStates[player.PlayerId].IsDead = false;
        Main.PlayerStates[player.PlayerId].deathReason = PlayerState.DeathReason.etc;

        player.RpcChangeRoleBasis(customRole, true);
        player.ResetKillCooldown();
        player.SyncSettings();
        player.SetKillCooldown();
        player.RpcResetAbilityCooldown();
        player.SyncGeneralOptions();

        Utils.DoNotifyRoles(SpecifySeer: player, NoCache: true);
    }
    /// <summary>
    /// Changes the Role Basis of player during the game
    /// </summary>
    /// <param name="newCustomRole">The custom role to change and auto set role type for others</param>
    public static void RpcChangeRoleBasis(this PlayerControl player, CustomRoles newCustomRole, bool loggerRoleMap = false)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInGame || player == null) return;

        var playerId = player.PlayerId;
        var playerClientId = player.GetClientId();
        var playerRole = player.GetCustomRole();
        var newRoleType = newCustomRole.GetRoleTypes();
        RoleTypes remeberRoleType;

        var oldRoleIsDesync = playerRole.IsDesyncRole();
        var newRoleIsDesync = newCustomRole.IsDesyncRole();

        var newVanillaRole = newCustomRole.GetVNRole();
        var newDesyncRole = newCustomRole.GetDYRole();

        switch (oldRoleIsDesync, newRoleIsDesync)
        {
            // Desync role to normal role
            case (true, false):
                {
                    foreach (var seer in Main.AllPlayerControls)
                    {
                        var seerClientId = seer.GetClientId();
                        if (seerClientId == -1) continue;
                        var seerIsHost = seer.IsHost();
                        var self = player.PlayerId == seer.PlayerId;

                        if (!self && seer.HasDesyncRole() && !seerIsHost)
                            remeberRoleType = newVanillaRole is CustomRoles.Noisemaker ? RoleTypes.Noisemaker : RoleTypes.Scientist;
                        else remeberRoleType = newRoleType;

                        // Set role type for seer
                        RpcSetRoleReplacer.RoleMap[(seer.PlayerId, playerId)] = (remeberRoleType, newCustomRole);
                        player.RpcSetRoleDesync(remeberRoleType, seerClientId);

                        if (self) continue;

                        var (seerRoleType, seerCustomRole) = seer.GetRoleMap();
                        if (seer.IsAlive())
                        {
                            if (seerCustomRole.IsDesyncRole())
                                remeberRoleType = seerIsHost ? RoleTypes.Crewmate : RoleTypes.Scientist;
                            else
                                remeberRoleType = seerRoleType;
                        }
                        else
                        {
                            remeberRoleType = RoleTypes.CrewmateGhost;
                            if (!newCustomRole.IsImpostor() && seer.Is(Custom_Team.Impostor)) remeberRoleType = RoleTypes.ImpostorGhost;

                            RpcSetRoleReplacer.RoleMap[(playerId, seer.PlayerId)] = (seerCustomRole.IsDesyncRole() ? seerIsHost ? RoleTypes.Crewmate : RoleTypes.Scientist : seerRoleType, seerCustomRole);
                            seer.RpcSetRoleDesync(remeberRoleType, playerClientId);
                            continue;
                        }

                        // Set role type for player
                        RpcSetRoleReplacer.RoleMap[(playerId, seer.PlayerId)] = (remeberRoleType, seerCustomRole);
                        seer.RpcSetRoleDesync(remeberRoleType, playerClientId);
                    }

                    break;
                }
            // Normal role to desync role
            case (false, true):
                {
                    foreach (var seer in Main.AllPlayerControls)
                    {
                        var seerClientId = seer.GetClientId();
                        if (seerClientId == -1) continue;
                        var self = player.PlayerId == seer.PlayerId;

                        if (self)
                        {
                            remeberRoleType = player.IsHost() ? RoleTypes.Crewmate : RoleTypes.Impostor;

                            // For Desync Shapeshifter
                            if (newDesyncRole is RoleTypes.Shapeshifter)
                                remeberRoleType = RoleTypes.Shapeshifter;
                        }
                        else
                        {
                            if (newRoleIsDesync) remeberRoleType = newVanillaRole is CustomRoles.Noisemaker ? RoleTypes.Noisemaker : RoleTypes.Scientist;
                            else remeberRoleType = newRoleType;
                        }

                        RpcSetRoleReplacer.RoleMap[(seer.PlayerId, playerId)] = (remeberRoleType, newCustomRole);
                        player.RpcSetRoleDesync(remeberRoleType, seerClientId);

                        if (self) continue;

                        var seerCustomRole = seer.GetRoleMap().CustomRole;
                        if (seer.IsAlive())
                        {
                            remeberRoleType = newVanillaRole is CustomRoles.Noisemaker ? RoleTypes.Noisemaker : RoleTypes.Scientist;
                        }
                        else
                        {
                            remeberRoleType = RoleTypes.CrewmateGhost;
                            RpcSetRoleReplacer.RoleMap[(playerId, seer.PlayerId)] = (seerCustomRole.GetVNRole() is CustomRoles.Noisemaker ? RoleTypes.Noisemaker : RoleTypes.Scientist, seerCustomRole);
                            seer.RpcSetRoleDesync(remeberRoleType, playerClientId);
                            continue;
                        }

                        // Set role type for player
                        RpcSetRoleReplacer.RoleMap[(playerId, seer.PlayerId)] = (remeberRoleType, seerCustomRole);
                        seer.RpcSetRoleDesync(remeberRoleType, playerClientId);
                    }

                    break;
                }
            // Desync role to desync role
            // Normal role to normal role
            default:
                {
                    var playerIsDesync = player.HasDesyncRole();
                    foreach (var seer in Main.AllPlayerControls)
                    {
                        var seerClientId = seer.GetClientId();
                        if (seerClientId == -1) continue;

                        if ((playerIsDesync || seer.HasDesyncRole()) && seer.PlayerId != playerId)
                            remeberRoleType = Utils.GetRoleMap(seer.PlayerId, playerId).RoleType;
                        else remeberRoleType = newRoleType;

                        RpcSetRoleReplacer.RoleMap[(seer.PlayerId, playerId)] = (remeberRoleType, newCustomRole);
                        player.RpcSetRoleDesync(remeberRoleType, seerClientId);
                    }

                    break;
                }
        }

        if (loggerRoleMap)
        {
            foreach (var seer in PlayerControl.AllPlayerControls.GetFastEnumerator())
            {
                var seerData = seer.Data;
                foreach (var target in PlayerControl.AllPlayerControls.GetFastEnumerator())
                {
                    var targetData = target.Data;
                    var (roleType, customRole) = seer.GetRoleMap(targetData.PlayerId);
                    Logger.Info($"seer {seerData?.PlayerName}-{seerData?.PlayerId}, target {targetData?.PlayerName}-{targetData?.PlayerId} => {roleType}, {customRole}", "Role Map");
                }
            }
        }

        Logger.Info($"{player.GetNameWithRole()}'s role basis was changed to {newRoleType} ({newCustomRole}) (from role: {playerRole}) - oldRoleIsDesync: {oldRoleIsDesync}, newRoleIsDesync: {newRoleIsDesync}", "RpcChangeRoleBasis");
    }
    public static void RpcSetPetDesync(this PlayerControl player, string petId, PlayerControl seer)
    {
        var clientId = seer.GetClientId();
        if (clientId == -1) return;
        if (AmongUsClient.Instance.ClientId == clientId)
        {
            player.SetPet(petId);
            return;
        }
        player.Data.DefaultOutfit.PetSequenceId += 10;
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.SetPetStr, SendOption.Reliable, clientId);
        writer.Write(petId);
        writer.Write(player.GetNextRpcSequenceId(RpcCalls.SetPetStr));
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcExile(this PlayerControl player)
    {
        player.Exiled();
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.Exiled, SendOption.None, -1);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcExileDesync(this PlayerControl player, PlayerControl seer)
    {
        var clientId = seer.GetClientId();
        if (AmongUsClient.Instance.ClientId == clientId)
        {
            player.Exiled();
            return;
        }
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.Exiled, SendOption.None, clientId);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcExileV2(this PlayerControl player)
    {
        if (player.Is(CustomRoles.Susceptible))
        {
            Susceptible.CallEnabledAndChange(player);
        }
        player.Exiled();

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.Exiled, SendOption.None, -1);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcCastVote(this PlayerControl player, byte suspectIdx)
    {
        if (!GameStates.IsMeeting)
        {
            Logger.Info($"Cancelled RpcCastVote for {player?.Data.PlayerName} because there is no meeting", "ExtendedPlayerControls..RPCCastVote");
            return;
        }
        if (player == null) return;
        var playerId = player.PlayerId;

        if (AmongUsClient.Instance.AmHost)
        {
            MeetingHud.Instance.CmdCastVote(playerId, suspectIdx);
        }
        else
        {
            var writer = CustomRpcSender.Create("Cast Vote", SendOption.Reliable);
            writer.AutoStartRpc(MeetingHud.Instance.NetId, (byte)RpcCalls.CastVote)
                .Write(playerId)
                .Write(suspectIdx)
            .EndRpc();
            writer.SendMessage();
        }
    }
    public static void RpcClearVoteDelay(this MeetingHud meeting, int clientId)
    {
        _ = new LateTask(() =>
        {
            if (meeting == null)
            {
                Logger.Info($"Cannot be cleared because meetinghud is null", "RpcClearVoteDelay");
                return;
            }
            if (AmongUsClient.Instance.ClientId == clientId)
            {
                meeting.ClearVote();
                return;
            }
            var writer = CustomRpcSender.Create("Clear Vote", SendOption.Reliable);
            writer.AutoStartRpc(meeting.NetId, (byte)RpcCalls.ClearVote, clientId).EndRpc();
            writer.SendMessage();
        }, 0.5f, "Clear Vote");
    }
    public static void RpcSetNameEx(this PlayerControl player, string name)
    {
        foreach (var seer in Main.AllPlayerControls)
        {
            Main.LastNotifyNames[(player.PlayerId, seer.PlayerId)] = name;
        }
        HudManagerPatch.LastSetNameDesyncCount++;

        Logger.Info($"Set:{player?.Data?.PlayerName}:{name} for All", "RpcSetNameEx");
        player.RpcSetName(name);
    }

    public static void RpcSetNamePrivate(this PlayerControl player, string name, PlayerControl seer = null, bool force = false)
    {
        //player: player whose name needs to be changed
        //seer: player who can see name changes
        if (player == null || name == null || !AmongUsClient.Instance.AmHost) return;
        if (seer == null) seer = player;

        if (!force && Main.LastNotifyNames[(player.PlayerId, seer.PlayerId)] == name)
        {
            //Logger.info($"Cancel:{player.name}:{name} for {seer.name}", "RpcSetNamePrivate");
            return;
        }
        Main.LastNotifyNames[(player.PlayerId, seer.PlayerId)] = name;
        HudManagerPatch.LastSetNameDesyncCount++;
        Logger.Info($"Set:{player?.Data?.PlayerName}:{name} for {seer.GetNameWithRole().RemoveHtmlTags()}", "RpcSetNamePrivate");

        if (seer == null || player == null) return;

        var leftPlayer = OnPlayerLeftPatch.LeftPlayerId;
        if (seer.PlayerId == leftPlayer || player.PlayerId == leftPlayer) return;

        var clientId = seer.GetClientId();
        if (clientId == -1) return;

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.SetName, SendOption.Reliable, clientId);
        writer.Write(seer.Data.NetId);
        writer.Write(name);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcEnterVentDesync(this PlayerPhysics physics, int ventId, PlayerControl seer)
    {
        if (physics == null) return;

        var clientId = seer.GetClientId();
        if (AmongUsClient.Instance.ClientId == clientId)
        {
            physics.StopAllCoroutines();
            physics.StartCoroutine(physics.CoEnterVent(ventId));
            return;
        }
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(physics.NetId, (byte)RpcCalls.EnterVent, SendOption.Reliable, seer.GetClientId());
        writer.WritePacked(ventId);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcExitVentDesync(this PlayerPhysics physics, int ventId, PlayerControl seer)
    {
        if (physics == null) return;

        var clientId = seer.GetClientId();
        if (AmongUsClient.Instance.ClientId == clientId)
        {
            physics.StopAllCoroutines();
            physics.StartCoroutine(physics.CoExitVent(ventId));
            return;
        }
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(physics.NetId, (byte)RpcCalls.ExitVent, SendOption.Reliable, seer.GetClientId());
        writer.WritePacked(ventId);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcBootFromVentDesync(this PlayerPhysics physics, int ventId, PlayerControl seer)
    {
        if (physics == null) return;

        var clientId = seer.GetClientId();
        if (AmongUsClient.Instance.ClientId == clientId)
        {
            physics.BootFromVent(ventId);
            return;
        }
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(physics.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, seer.GetClientId());
        writer.WritePacked(ventId);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    /// <summary>
    /// ONLY to be used when killer surely may kill the target, please check with killer.RpcCheckAndMurder(target, check: true) for indirect kill.
    /// </summary>
    public static void RpcMurderPlayer(this PlayerControl killer, PlayerControl target)
    {
        // If Target is Dollmaster or Possessed Player run Dollmasters kill check instead.
        if (DollMaster.SwapPlayerInfo(target) != target)
        {
            DollMaster.CheckMurderAsPossessed(killer, target);
            return;
        }

        killer.RpcMurderPlayer(target, true);
    }
    public static void RpcGuardAndKill(this PlayerControl killer, PlayerControl target = null, bool forObserver = false, bool fromSetKCD = false)
    {
        if (!AmongUsClient.Instance.AmHost)
        {
            var caller = new System.Diagnostics.StackFrame(1, false);
            var callerMethod = caller.GetMethod();
            string callerMethodName = callerMethod.Name;
            string callerClassName = callerMethod.DeclaringType.FullName;
            Logger.Warn($"Modded non-host client activated RpcGuardAndKill from {callerClassName}.{callerMethodName}", "RpcGuardAndKill");
            return;
        }

        if (target == null) target = killer;

        // Check Observer
        if (Observer.HasEnabled && !forObserver && !MeetingStates.FirstMeeting)
        {
            Observer.ActivateGuardAnimation(killer.PlayerId, target);
        }

        // Host
        if (killer.IsHost())
        {
            killer.MurderPlayer(target, MurderResultFlags.FailedProtected);
        }
        // Other Clients
        else
        {
            var writer = AmongUsClient.Instance.StartRpcImmediately(killer.NetId, (byte)RpcCalls.MurderPlayer, SendOption.Reliable, killer.GetClientId());
            writer.WriteNetObject(target);
            writer.Write((int)MurderResultFlags.FailedProtected);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }

        if (!fromSetKCD) killer.SetKillTimer(half: true);
    }
    public static void SetKillCooldown(this PlayerControl player, float time = -1f, PlayerControl target = null, bool forceAnime = false)
    {
        if (player == null) return;

        if (!player.HasImpKillButton(considerVanillaShift: true)) return;
        if (player.HasImpKillButton(false) && !player.CanUseKillButton()) return;
        
        player.SetKillTimer(CD: time);
        if (target == null) target = player;
        if (time >= 0f) Main.AllPlayerKillCooldown[player.PlayerId] = time * 2;
        else Main.AllPlayerKillCooldown[player.PlayerId] *= 2;
        if (player.GetRoleClass() is Glitch gc)
        {
            if (gc.NotSetCD)
            {
                gc.NotSetCD = false;
            }
            else
            {
                gc.LastKill = Utils.GetTimeStamp() + ((int)(time / 2) - Glitch.KillCooldown.GetInt());
                gc.KCDTimer = (int)(time / 2);
            }
        }
        else if (forceAnime || !player.IsModded() || !Options.DisableShieldAnimations.GetBool())
        {
            player.SyncSettings();
            player.RpcGuardAndKill(target, fromSetKCD: true);
        }
        else
        {
            time = Main.AllPlayerKillCooldown[player.PlayerId] / 2;
            if (player.IsHost()) PlayerControl.LocalPlayer.SetKillTimer(time);
            else
            {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetKillTimer, SendOption.Reliable, player.GetClientId());
                writer.Write(time);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }
            // Check Observer
            if (Observer.HasEnabled)
            {
                Observer.ActivateGuardAnimation(target.PlayerId, target);
            }
        }
        player.ResetKillCooldown();
    }
    public static void SetKillCooldownV2(this PlayerControl player, float time = -1f)
    {
        if (player == null) return;
        if (!player.CanUseKillButton()) return;
        player.SetKillTimer(CD: time);
        if (time >= 0f) Main.AllPlayerKillCooldown[player.PlayerId] = time * 2;
        else Main.AllPlayerKillCooldown[player.PlayerId] *= 2;
        player.SyncSettings();
        player.RpcGuardAndKill(fromSetKCD: true);
        player.ResetKillCooldown();
    }
    public static void SetKillCooldownV3(this PlayerControl player, float time = -1f, PlayerControl target = null, bool forceAnime = false)
    {
        if (player == null) return;
        if (!player.CanUseKillButton()) return;
        player.SetKillTimer(CD: time);
        if (target == null) target = player;
        if (time >= 0f) Main.AllPlayerKillCooldown[player.PlayerId] = time * 2;
        else Main.AllPlayerKillCooldown[player.PlayerId] *= 2;
        if (forceAnime || !player.IsModded() || !Options.DisableShieldAnimations.GetBool())
        {
            player.SyncSettings();
            player.RpcGuardAndKill(target, fromSetKCD: true);
        }
        else
        {
            time = Main.AllPlayerKillCooldown[player.PlayerId] / 2;
            if (player.IsHost()) PlayerControl.LocalPlayer.SetKillTimer(time);
            else
            {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetKillTimer, SendOption.Reliable, player.GetClientId());
                writer.Write(time);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }
            // Check Observer
            if (Observer.HasEnabled)
            {
                Observer.ActivateGuardAnimation(target.PlayerId, target);
            }
        }
        player.ResetKillCooldown();
    }
    public static void RpcSpecificShapeshift(this PlayerControl player, PlayerControl target, bool shouldAnimate)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (player.IsHost())
        {
            player.Shapeshift(target, shouldAnimate);
            return;
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.Shapeshift, SendOption.Reliable, player.GetClientId());
        messageWriter.WriteNetObject(target);
        messageWriter.Write(shouldAnimate);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }
    public static void RpcSpecificRejectShapeshift(this PlayerControl player, PlayerControl target, bool shouldAnimate)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        foreach (var seer in Main.AllPlayerControls)
        {
            if (seer != player)
            {
                MessageWriter msg = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.RejectShapeshift, SendOption.Reliable, seer.GetClientId());
                AmongUsClient.Instance.FinishRpcImmediately(msg);
            }
            else
            {
                player.RpcSpecificShapeshift(target, shouldAnimate);
            }
        }
    }

    public static Vent GetClosestVent(this PlayerControl player)
    {
        var pos = player.GetCustomPosition();
        return ShipStatus.Instance.AllVents.Where(x => x != null).MinBy(x => Vector2.Distance(pos, x.transform.position));
    }
    
    public static List<Vent> GetVentsFromClosest(this PlayerControl player)
    {
        Vector2 playerpos = player.transform.position;
        List<Vent> vents = new(ShipStatus.Instance.AllVents);
        vents.Sort((v1, v2) => Vector2.Distance(playerpos, v1.transform.position).CompareTo(Vector2.Distance(playerpos, v2.transform.position)));

        // If player is inside a vent, we get the nearby vents that the player can snapto and insert them to the top of the list
        // Idk how to directly get the vent a player is in, so just assume the closet vent from the player is the vent that player is in
        // Not sure about whether inVent flags works 100% correct here. Maybe player is being kicked from a vent and inVent flags can return true there
        if ((player.MyPhysics.Animations.IsPlayingEnterVentAnimation() || player.walkingToVent || player.inVent) && vents[0] != null)
        {
            var nextvents = vents[0].NearbyVents.ToList();
            nextvents.RemoveAll(v => v == null);

            foreach (var vent in nextvents)
            {
                vents.Remove(vent);
            }

            vents.InsertRange(0, nextvents.FindAll(v => v != null));
        }

        return vents;
    }

    /// <summary>
    /// Update vent interaction if player again can use vent
    /// Or vice versa if he cannot use it
    /// </summary>
    public static void RpcSetVentInteraction(this PlayerControl player)
    {
        VentSystemDeterioratePatch.SerializeV2(ShipStatus.Instance.Systems[SystemTypes.Ventilation].Cast<VentilationSystem>(), player);
    }
    public static void RpcSetSpecificScanner(this PlayerControl target, PlayerControl seer, bool IsActive)
    {
        var seerClientId = seer.GetClientId();
        if (seerClientId == -1) return;
        byte cnt = ++target.scannerCount;
        if (AmongUsClient.Instance.ClientId == seerClientId)
        {
            target.SetScanner(IsActive, cnt);
            return;
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(target.NetId, (byte)RpcCalls.SetScanner, SendOption.Reliable, seerClientId);
        messageWriter.Write(IsActive);
        messageWriter.Write(cnt);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);

        target.scannerCount = cnt;
    }
    public static void RpcCheckVanishDesync(this PlayerControl player, PlayerControl seer)
    {
        if (AmongUsClient.Instance.ClientId == seer.GetClientId())
        {
            player.CheckVanish();
            return;
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.CheckVanish, SendOption.None, seer.GetClientId());
        messageWriter.Write(0); // not used, lol
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }
    public static void RpcStartVanishDesync(this PlayerControl player, PlayerControl seer)
    {
        if (AmongUsClient.Instance.ClientId == seer.GetClientId())
        {
            player.SetRoleInvisibility(true, false, true);
            return;
        }
        MessageWriter msg = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.StartVanish, SendOption.None, seer.GetClientId());
        AmongUsClient.Instance.FinishRpcImmediately(msg);
    }
    public static void RpcCheckAppearDesync(this PlayerControl player, bool shouldAnimate, PlayerControl seer)
    {
        if (AmongUsClient.Instance.ClientId == seer.GetClientId())
        {
            player.CheckAppear(shouldAnimate);
            return;
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.CheckAppear, SendOption.None, seer.GetClientId());
        messageWriter.Write(shouldAnimate);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }
    public static void RpcStartAppearDesync(this PlayerControl player, bool shouldAnimate, PlayerControl seer)
    {
        if (AmongUsClient.Instance.ClientId == seer.GetClientId())
        {
            player.SetRoleInvisibility(false, shouldAnimate, true);
            return;
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.StartAppear, SendOption.None, seer.GetClientId());
        messageWriter.Write(shouldAnimate);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }
    public static void RpcCheckAppear(this PlayerControl player, bool shouldAnimate)
    {
        player.CheckAppear(shouldAnimate);
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.CheckAppear, SendOption.None);
        messageWriter.Write(shouldAnimate);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }
    public static void RpcSpecificMurderPlayer(this PlayerControl killer, PlayerControl target, PlayerControl seer)
    {
        if (seer.IsHost())
        {
            killer.MurderPlayer(target, MurderResultFlags.Succeeded);
            return;
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(killer.NetId, (byte)RpcCalls.MurderPlayer, SendOption.None, seer.GetClientId());
        messageWriter.WriteNetObject(target);
        messageWriter.Write((int)MurderResultFlags.Succeeded);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    } //Must provide seer, target
    public static void RpcSpecificProtectPlayer(this PlayerControl killer, PlayerControl target = null, int colorId = 0)
    {
        if (AmongUsClient.Instance.AmClient)
        {
            killer.ProtectPlayer(target, colorId);
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(killer.NetId, (byte)RpcCalls.ProtectPlayer, SendOption.Reliable, killer.GetClientId());
        messageWriter.WriteNetObject(target);
        messageWriter.Write(colorId);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }
    public static void RpcResetAbilityCooldown(this PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost || target == null) return; // Nothing happens when run by anyone other than the host.
        Logger.Info($"Ability cooldown reset: {target.name}({target.PlayerId})", "RpcResetAbilityCooldown");

        if (target.GetRoleClass() is Glitch gc)
        {
            gc.LastHack = Utils.GetTimeStamp();
            gc.LastMimic = Utils.GetTimeStamp();
            gc.MimicCDTimer = 10;
            gc.HackCDTimer = 10;
        }
        else if (PlayerControl.LocalPlayer.PlayerId == target.PlayerId)
        {
            //if target is the host, except for guardian angel, that breaks it.
            PlayerControl.LocalPlayer.Data.Role.SetCooldown();
        }
        else
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(target.NetId, (byte)RpcCalls.ProtectPlayer, SendOption.None, target.GetClientId());
            writer.WriteNetObject(target);
            writer.Write(0);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        /*
            When a player puts up a barrier, the cooldown of the ability is reset regardless of the player's position.
            Due to the addition of logs, it is no longer possible to put up a barrier to nothing, so we have changed it to put up a 0 second barrier to oneself instead.
            This change disables guardian angel as a position.
            The cooldown of the host resets directly.
        */
    }
    public static void RpcDesyncUpdateSystem(this PlayerControl target, SystemTypes systemType, int amount)
    {
        var messageWriter = AmongUsClient.Instance.StartRpcImmediately(ShipStatus.Instance.NetId, (byte)RpcCalls.UpdateSystem, SendOption.Reliable, target.GetClientId());
        messageWriter.Write((byte)systemType);
        messageWriter.WriteNetObject(target);
        messageWriter.Write((byte)amount);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }

    public static void RpcTeleportAllPlayers(Vector2 location)
    {
        foreach (var pc in Main.AllAlivePlayerControls)
        {
            pc.RpcTeleport(location);
        }
    }
    public static void RpcDesyncTeleport(this PlayerControl player, Vector2 position, PlayerControl seer)
    {
        if (player == null) return;
        var netTransform = player.NetTransform;
        var clientId = seer.GetClientId();
        if (AmongUsClient.Instance.ClientId == clientId)
        {
            netTransform.SnapTo(position, (ushort)(6 + netTransform.lastSequenceId));
            return;
        }
        netTransform.lastSequenceId += 326;
        netTransform.SetDirtyBit(uint.MaxValue);

        ushort newSid = (ushort)(8 + netTransform.lastSequenceId);
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(netTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.Reliable, clientId);
        NetHelpers.WriteVector2(position, writer);
        writer.Write(newSid);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void RpcTeleport(this PlayerControl player, Vector2 position, bool isRandomSpawn = false, bool sendInfoInLogs = true)
    {
        if (sendInfoInLogs)
        {
            Logger.Info($" {player.GetNameWithRole().RemoveHtmlTags()} => {position}", "RpcTeleport");
            Logger.Info($" Player Id: {player.PlayerId}", "RpcTeleport");
        }

        // Don't check player status during random spawn
        if (!isRandomSpawn)
        {
            var cancelTeleport = false;

            if (player.inVent || player.MyPhysics.Animations.IsPlayingEnterVentAnimation())
            {
                Logger.Info($"Target: ({player.GetNameWithRole().RemoveHtmlTags()}) in vent", "RpcTeleport");
                cancelTeleport = true;
            }
            else if (player.onLadder || player.MyPhysics.Animations.IsPlayingAnyLadderAnimation())
            {
                Logger.Warn($"Teleporting canceled - Target: ({player.GetNameWithRole().RemoveHtmlTags()}) is in on Ladder", "RpcTeleport");
                cancelTeleport = true;
            }
            else if (player.inMovingPlat)
            {
                Logger.Warn($"Teleporting canceled - Target: ({player.GetNameWithRole().RemoveHtmlTags()}) use moving platform (Airship/Fungle)", "RpcTeleport");
                cancelTeleport = true;
            }

            if (cancelTeleport)
            {
                player.Notify(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), GetString("ErrorTeleport")));
                return;
            }
        }

        var netTransform = player.NetTransform;

        if (AmongUsClient.Instance.AmHost)
        {
            // +328 because lastSequenceId has delay between the host and the vanilla client
            // And this cannot forced teleport the player
            netTransform.SnapTo(position, (ushort)(netTransform.lastSequenceId + 328));
            netTransform.SetDirtyBit(uint.MaxValue);
        }

        ushort newSid = (ushort)(netTransform.lastSequenceId + 8);
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(netTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.Reliable);
        NetHelpers.WriteVector2(position, messageWriter);
        messageWriter.Write(newSid);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
    }
    public static void RpcRandomVentTeleport(this PlayerControl player)
    {
        var vents = ShipStatus.Instance.AllVents;
        var vent = vents.RandomElement();

        Logger.Info($" {vent.transform.position}", "RpcVentTeleportPosition");
        player.RpcTeleport(new Vector2(vent.transform.position.x, vent.transform.position.y + 0.3636f));
    }

    public static ClientData GetClient(this PlayerControl player)
    {
        try
        {
            var client = AmongUsClient.Instance.allClients.ToArray().FirstOrDefault(cd => cd.Character.PlayerId == player.PlayerId);
            return client;
        }
        catch
        {
            return null;
        }
    }
    public static int GetClientId(this PlayerControl player)
    {
        if (player == null) return -1;
        var data = player.Data;
        return data == null ? -1 : data.ClientId;
    }
    public static int GetClientId(this NetworkedPlayerInfo playerData) => playerData == null ? -1 : playerData.ClientId;

    /// <summary>
    /// Only roles (no add-ons)
    /// </summary>
    public static CustomRoles GetCustomRole(this NetworkedPlayerInfo player) => player == null || player.Object == null ? CustomRoles.Crewmate : player.Object.GetCustomRole();
    /// <summary>
    /// Only roles (no add-ons)
    /// </summary>
    public static CustomRoles GetCustomRole(this PlayerControl player)
    {
        if (player == null)
        {
            var caller = new System.Diagnostics.StackFrame(1, false);
            var callerMethod = caller.GetMethod();
            string callerMethodName = callerMethod.Name;
            string callerClassName = callerMethod.DeclaringType.FullName;
            Logger.Warn($"{callerClassName}.{callerMethodName} tried to retrieve CustomRole, but the target was null", "GetCustomRole");
            return CustomRoles.Crewmate;
        }
        return Main.PlayerStates.TryGetValue(player.PlayerId, out var State) ? State.MainRole : CustomRoles.Crewmate;
    }

    public static List<CustomRoles> GetCustomSubRoles(this PlayerControl player)
    {
        if (player == null)
        {
            var caller = new System.Diagnostics.StackFrame(1, false);
            var callerMethod = caller.GetMethod();
            string callerMethodName = callerMethod.Name;
            string callerClassName = callerMethod.DeclaringType.FullName;
            Logger.Warn($"{callerClassName}.{callerMethodName} tried to get CustomSubRole, but the target was null", "GetCustomRole");
            return [CustomRoles.NotAssigned];
        }
        return Main.PlayerStates.TryGetValue(player.PlayerId, out var State) ? State.SubRoles : [CustomRoles.NotAssigned];
    }
    public static CountTypes GetCountTypes(this PlayerControl player)
    {
        if (player == null)
        {
            var caller = new System.Diagnostics.StackFrame(1, false);
            var callerMethod = caller.GetMethod();
            string callerMethodName = callerMethod.Name;
            string callerClassName = callerMethod.DeclaringType.FullName;
            Logger.Warn($"{callerClassName}.{callerMethodName} tried to get CountTypes, but the target was null", "GetCountTypes");
            return CountTypes.None;
        }

        return Main.PlayerStates.TryGetValue(player.PlayerId, out var State) ? State.countTypes : CountTypes.None;
    }
    public static DeadBody GetDeadBody(this NetworkedPlayerInfo playerData)
    {
        return UnityEngine.Object.FindObjectsOfType<DeadBody>().FirstOrDefault(bead => bead.ParentId == playerData.PlayerId);
    }
    public static void MarkDirtySettings(this PlayerControl player)
    {
        PlayerGameOptionsSender.SetDirty(player.PlayerId);
    }
    public static void SyncSettings(this PlayerControl player)
    {
        PlayerGameOptionsSender.SetDirty(player.PlayerId);
        GameOptionsSender.SendAllGameOptions();
    }
    public static TaskState GetPlayerTaskState(this PlayerControl player)
    {
        return Main.PlayerStates[player.PlayerId].TaskState;
    }
    public static string GetDisplayRoleAndSubName(this PlayerControl seer, PlayerControl target, bool notShowAddOns = false)
    {
        return Utils.GetDisplayRoleAndSubName(seer.PlayerId, target.PlayerId, notShowAddOns);
    }
    public static string GetSubRoleName(this PlayerControl player, bool forUser = false)
    {
        if (GameStates.IsHideNSeek) return string.Empty;

        var SubRoles = Main.PlayerStates[player.PlayerId].SubRoles.ToArray();
        if (SubRoles.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var role in SubRoles)
        {
            if (role == CustomRoles.NotAssigned) continue;
            sb.Append($"{Utils.ColorString(Color.white, " + ")}{Utils.GetRoleName(role, forUser)}");
        }

        return sb.ToString();
    }
    public static string GetAllRoleName(this PlayerControl player, bool forUser = true)
    {   
        if (!player) return null;
        var text = Utils.GetRoleName(player.GetCustomRole(), forUser);
        text += player.GetSubRoleName(forUser);
        return text;
    }
    public static string GetNameWithRole(this PlayerControl player, bool forUser = false)
    {
        if (!forUser)
        {
            return GetRealName(player);
        }
        return $"{player?.Data?.PlayerName}" + (GameStates.IsInGame && Options.CurrentGameMode != CustomGameMode.FFA ? $"({player?.GetAllRoleName(forUser)})" : string.Empty);
    }
    public static string GetRoleColorCode(this PlayerControl player)
    {
        return Utils.GetRoleColorCode(player.GetCustomRole());
    }
    public static Color GetRoleColor(this PlayerControl player)
    {
        return Utils.GetRoleColor(player.GetCustomRole());
    }
    public static void ResetPlayerCam(this PlayerControl pc, float delay = 0f)
    {
        if (pc == null || !AmongUsClient.Instance.AmHost || pc.IsModded()) return;

        var systemtypes = Utils.GetCriticalSabotageSystemType();

        _ = new LateTask(() =>
        {
            pc.RpcDesyncUpdateSystem(systemtypes, 128);
        }, 0f + delay, "Reactor Desync");

        _ = new LateTask(() =>
        {
            pc.RpcSpecificMurderPlayer(pc, pc);
        }, 0.2f + delay, "Murder To Reset Cam");

        _ = new LateTask(() =>
        {
            pc.RpcDesyncUpdateSystem(systemtypes, 16);

            if (GameStates.AirshipIsActive)
                pc.RpcDesyncUpdateSystem(systemtypes, 17);
        }, 0.4f + delay, "Fix Desync Reactor");
    }
    public static void ReactorFlash(this PlayerControl pc, float delay = 0f)
    {
        if (pc == null) return;
        // Logger.Info($"{pc}", "ReactorFlash");
        var systemtypes = Utils.GetCriticalSabotageSystemType();
        float FlashDuration = Options.KillFlashDuration.GetFloat();

        pc.RpcDesyncUpdateSystem(systemtypes, 128);

        _ = new LateTask(() =>
        {
            pc.RpcDesyncUpdateSystem(systemtypes, 16);

            if (GameStates.AirshipIsActive)
                pc.RpcDesyncUpdateSystem(systemtypes, 17);

        }, FlashDuration + delay, "Fix Desync Reactor");
    }

    public static string GetRealName(this PlayerControl player, bool isMeeting = false, bool clientData = false)
    {
        if (clientData)
        {
            var client = player.GetClient();

            if (client != null)
            {
                if (Main.AllClientRealNames.TryGetValue(client.Id, out var realname))
                {
                    return realname;
                }
                return player.GetClient().PlayerName;
            }
        }
        return isMeeting || player == null ? player?.Data?.PlayerName : player?.name;
    }
    public static bool CanUseKillButton(this PlayerControl pc)
    {
        if (GameStates.IsLobby) return false;
        if (!pc.IsAlive() || Pelican.IsEaten(pc.PlayerId) || DollMaster.IsDoll(pc.PlayerId)) return false;
        if (pc.GetClient().GetHashedPuid() == Main.FirstDiedPrevious && !Options.ShieldedCanUseKillButton.GetBool() && MeetingStates.FirstMeeting) return false;
        if (pc.Is(CustomRoles.Killer) || Mastermind.PlayerIsManipulated(pc)) return true;

        var playerRoleClass = pc.GetRoleClass();
        if (playerRoleClass != null && playerRoleClass.CanUseKillButton(pc)) return true;

        return false;
    }
    public static bool HasKillButton(this PlayerControl pc)
    {
        if (pc == null) return false;
        if (!pc.IsAlive() || pc.Data.Role.Role == RoleTypes.GuardianAngel || Pelican.IsEaten(pc.PlayerId)) return false;
        
        var role = pc.GetCustomRole();
        if (!role.IsImpostor())
        {
            return role.GetDYRole() is RoleTypes.Impostor or RoleTypes.Shapeshifter;
        }
        return role.GetVNRole() switch
        { 
            CustomRoles.Impostor => true,
            CustomRoles.Shapeshifter => true,
            CustomRoles.Phantom => true,
            _ => false
        };
    }
    public static bool CanUseVents(this PlayerControl player) => player != null && (player.CanUseImpostorVentButton() || player.GetCustomRole().GetVNRole() == CustomRoles.Engineer);
    public static bool CantUseVent(this PlayerControl player, int ventId) => player == null || !player.CanUseVents() || (CustomRoleManager.BlockedVentsList.TryGetValue(player.PlayerId, out var blockedVents) && blockedVents.Contains(ventId));
    public static bool HasAnyBlockedVent(this PlayerControl player) => player != null && CustomRoleManager.BlockedVentsList.TryGetValue(player.PlayerId, out var blockedVents) && blockedVents.Any();
    public static bool NotUnlockVent(this PlayerControl player, int ventId) => player != null && CustomRoleManager.DoNotUnlockVentsList.TryGetValue(player.PlayerId, out var blockedVents) && blockedVents.Contains(ventId);

    public static bool CanUseImpostorVentButton(this PlayerControl pc)
    {
        if (!pc.IsAlive()) return false;
        if (GameStates.IsHideNSeek) return true;
        if (pc.Is(CustomRoles.Killer) || pc.Is(CustomRoles.Nimble)) return true;
        if (DollMaster.IsDoll(pc.PlayerId) || Circumvent.CantUseVent(pc)) return false;
        if (Necromancer.Killer && !pc.Is(CustomRoles.Necromancer)) return false;
        if (Amnesiac.PreviousAmnesiacCanVent(pc)) return true; //this is done because amnesiac has imp basis and if amnesiac remembers a role with different basis then player will not vent as `CanUseImpostorVentButton` is false

        var playerRoleClass = pc.GetRoleClass();
        if (playerRoleClass != null && playerRoleClass.CanUseImpostorVentButton(pc)) return true;

        return false;
    }
    public static bool CanUseSabotage(this PlayerControl pc)
    {
        if (pc.Is(Custom_Team.Impostor) && !pc.IsAlive() && Options.DeadImpCantSabotage.GetBool()) return false;

        var playerRoleClass = pc.GetRoleClass();
        if (playerRoleClass != null && playerRoleClass.CanUseSabotage(pc)) return true;

        return false;
    }
    public static void ResetKillCooldown(this PlayerControl player)
    {
        Main.AllPlayerKillCooldown[player.PlayerId] = Options.DefaultKillCooldown;

        // FFA
        if (player.Is(CustomRoles.Killer))
        {
            Main.AllPlayerKillCooldown[player.PlayerId] = FFAManager.FFA_KCD.GetFloat();
        }
        else
        {
            player.GetRoleClass()?.SetKillCooldown(player.PlayerId);
        }

        var playerSubRoles = player.GetCustomSubRoles();

        if (playerSubRoles.Any())
            foreach (var subRole in playerSubRoles)
            {
                switch (subRole)
                {
                    case CustomRoles.LastImpostor when player.PlayerId == LastImpostor.currentId:
                        LastImpostor.SetKillCooldown();
                        break;

                    case CustomRoles.Mare:
                        Main.AllPlayerKillCooldown[player.PlayerId] = Mare.KillCooldownInLightsOut.GetFloat();
                        break;

                    case CustomRoles.Overclocked:
                        Main.AllPlayerKillCooldown[player.PlayerId] -= Main.AllPlayerKillCooldown[player.PlayerId] * (Overclocked.OverclockedReduction.GetFloat() / 100);
                        break;

                    case CustomRoles.Diseased:
                        Diseased.IncreaseKCD(player);
                        break;

                    case CustomRoles.Antidote:
                        Antidote.ReduceKCD(player);
                        break;
                }
            }

        if (!player.HasImpKillButton(considerVanillaShift: false))
            Main.AllPlayerKillCooldown[player.PlayerId] = 300f;

        if (Main.AllPlayerKillCooldown[player.PlayerId] == 0)
        {
            Main.AllPlayerKillCooldown[player.PlayerId] = 0.3f;
        }
    }
    public static bool IsNonCrewSheriff(this PlayerControl sheriff)
    {
        return sheriff.Is(CustomRoles.Madmate)
            || sheriff.Is(CustomRoles.Charmed)
            || sheriff.Is(CustomRoles.Infected)
            || sheriff.Is(CustomRoles.Contagious)
            || sheriff.Is(CustomRoles.Egoist);
    }
    public static bool ShouldBeDisplayed(this CustomRoles subRole)
    {
        return subRole is not 
            CustomRoles.LastImpostor and not
            CustomRoles.Madmate and not
            CustomRoles.Charmed and not
            CustomRoles.Recruit and not
            CustomRoles.Admired and not
            CustomRoles.Soulless and not
            CustomRoles.Lovers and not
            CustomRoles.Infected and not
            CustomRoles.Contagious;
    }

    public static void AddInSwitchAddons(this PlayerControl Killed, PlayerControl target, CustomRoles Addon = CustomRoles.NotAssigned, CustomRoles? IsAddon = CustomRoles.NotAssigned)
    {
        if (Addon == CustomRoles.NotAssigned)
        {
            Addon = IsAddon ?? CustomRoles.NotAssigned;
        }
        if (CustomRoleManager.AddonClasses.TryGetValue(Addon, out var IAddon))
        {
            IAddon?.Remove(Killed.PlayerId);
            IAddon?.Add(target.PlayerId, false);
        }
    }
    public static bool RpcCheckAndMurder(this PlayerControl killer, PlayerControl target, bool check = false)
    {
        var caller = new System.Diagnostics.StackFrame(1, false);
        var callerMethod = caller.GetMethod();
        string callerMethodName = callerMethod.Name;
        string callerClassName = callerMethod.DeclaringType.FullName;
        Logger.Msg($"RpcCheckAndMurder activated from: {callerClassName}.{callerMethodName}", "RpcCheckAndMurder");

        return CheckMurderPatch.RpcCheckAndMurder(killer, target, check);
    }
    public static bool CheckForInvalidMurdering(this PlayerControl killer, PlayerControl target, bool checkCanUseKillButton = false) => CheckMurderPatch.CheckForInvalidMurdering(killer, target, checkCanUseKillButton);
    public static void NoCheckStartMeeting(this PlayerControl reporter, NetworkedPlayerInfo target, bool force = false)
    { 
        //Method that can cause a meeting to occur regardless of whether it is in sabotage.
        //If target is null, it becomes a button.
        if (Options.DisableMeeting.GetBool() && !force) return;

        SetUpRoleTextPatch.IsInIntro = false;
        ReportDeadBodyPatch.AfterReportTasks(reporter, target, true);
        MeetingRoomManager.Instance.AssignSelf(reporter, target);
        DestroyableSingleton<HudManager>.Instance.OpenMeetingRoom(reporter);
        reporter.RpcStartMeeting(target);
    }
    public static bool IsHost(this InnerNetObject innerObject) => innerObject.OwnerId == AmongUsClient.Instance.HostId;
    public static bool IsHost(this byte playerId) => playerId.GetPlayer()?.OwnerId == AmongUsClient.Instance.HostId;
    public static bool IsModded(this PlayerControl player) => player != null && (player.AmOwner || player.IsHost() || Main.playerVersion.ContainsKey(player.GetClientId()));
    public static bool IsNonHostModdedClient(this PlayerControl pc) => pc != null && !pc.IsHost() && Main.playerVersion.ContainsKey(pc.GetClientId());
    ///<summary>
    ///プレイヤーのRoleBehaviourのGetPlayersInAbilityRangeSortedを実行し、戻り値を返します。
    ///</summary>
    ///<param name="ignoreColliders">trueにすると、壁の向こう側のプレイヤーが含まれるようになります。守護天使用</param>
    ///<returns>GetPlayersInAbilityRangeSortedの戻り値</returns>
    public static List<PlayerControl> GetPlayersInAbilityRangeSorted(this PlayerControl player, bool ignoreColliders = false) => GetPlayersInAbilityRangeSorted(player, pc => true, ignoreColliders);
    ///<summary>
    ///プレイヤーのRoleBehaviourのGetPlayersInAbilityRangeSortedを実行し、predicateの条件に合わないものを除外して返します。
    ///</summary>
    ///<param name="predicate">リストに入れるプレイヤーの条件 このpredicateに入れてfalseを返すプレイヤーは除外されます。</param>
    ///<param name="ignoreColliders">trueにすると、壁の向こう側のプレイヤーが含まれるようになります。守護天使用</param>
    ///<returns>GetPlayersInAbilityRangeSortedの戻り値から条件に合わないプレイヤーを除外したもの。</returns>
    public static List<PlayerControl> GetPlayersInAbilityRangeSorted(this PlayerControl player, Predicate<PlayerControl> predicate, bool ignoreColliders = false)
    {
        var rangePlayersIL = RoleBehaviour.GetTempPlayerList();
        List<PlayerControl> rangePlayers = [];
        player.Data.Role.GetPlayersInAbilityRangeSorted(rangePlayersIL, ignoreColliders);
        foreach (var pc in rangePlayersIL.GetFastEnumerator())
        {
            if (predicate(pc)) rangePlayers.Add(pc);
        }
        return rangePlayers;
    }
    public static bool IsNeutralKiller(this PlayerControl player) => player.GetCustomRole().IsNK();
    public static bool IsNeutralBenign(this PlayerControl player) => player.GetCustomRole().IsNB();
    public static bool IsNeutralEvil(this PlayerControl player) => player.GetCustomRole().IsNE();
    public static bool IsNeutralChaos(this PlayerControl player) => player.GetCustomRole().IsNC();
    public static bool IsNeutralApocalypse(this PlayerControl player) => player.GetCustomRole().IsNA();
    public static bool IsTransformedNeutralApocalypse(this PlayerControl player) => player.GetCustomRole().IsTNA();
    public static bool IsNonNeutralKiller(this PlayerControl player) => player.GetCustomRole().IsNonNK();

    public static bool IsMurderedThisRound(this PlayerControl player) => player.PlayerId.IsMurderedThisRound();
    public static bool IsMurderedThisRound(this byte playerId) => Main.MurderedThisRound.Contains(playerId);

    public static bool KnowDeathReason(this PlayerControl seer, PlayerControl target)
        => (Options.EveryoneCanSeeDeathReason.GetBool()
        || seer.Is(CustomRoles.Doctor) || seer.Is(CustomRoles.Autopsy)
        || (seer.Data.IsDead && Options.GhostCanSeeDeathReason.GetBool()))
        && target.Data.IsDead || target.Is(CustomRoles.Gravestone) && target.Data.IsDead;

    public static bool KnowDeadTeam(this PlayerControl seer, PlayerControl target)
        => (seer.Is(CustomRoles.Necroview))
        && target.Data.IsDead;

    public static bool KnowLivingTeam(this PlayerControl seer, PlayerControl target)
        => (seer.Is(CustomRoles.Visionary))
        && !target.Data.IsDead;

    private readonly static LogHandler logger = Logger.Handler("KnowRoleTarget");
    public static bool KnowRoleTarget(PlayerControl seer, PlayerControl target)
    {
        if (Options.CurrentGameMode == CustomGameMode.FFA || GameEndCheckerForNormal.GameIsEnded) return true;
        else if (seer.Is(CustomRoles.GM) || target.Is(CustomRoles.GM) || (PlayerControl.LocalPlayer.PlayerId == seer.PlayerId && Main.GodMode.Value)) return true;
        else if (Options.SeeEjectedRolesInMeeting.GetBool() && Main.PlayerStates[target.PlayerId].deathReason == PlayerState.DeathReason.Vote) return true;
        else if (Altruist.HasEnabled && seer.IsMurderedThisRound()) return false;
        else if (Main.VisibleTasksCount && !seer.IsAlive() && Options.GhostCanSeeOtherRoles.GetBool()) return true;
        else if (seer.GetCustomRole() == target.GetCustomRole() && seer.GetCustomRole().IsNK()) return true;
        else if (Options.LoverKnowRoles.GetBool() && seer.Is(CustomRoles.Lovers) && target.Is(CustomRoles.Lovers)) return true;
        else if (Options.ImpsCanSeeEachOthersRoles.GetBool() && seer.Is(Custom_Team.Impostor) && target.Is(Custom_Team.Impostor)) return true;
        else if (Madmate.MadmateKnowWhosImp.GetBool() && seer.Is(CustomRoles.Madmate) && target.Is(Custom_Team.Impostor)) return true;
        else if (Madmate.ImpKnowWhosMadmate.GetBool() && target.Is(CustomRoles.Madmate) && seer.Is(Custom_Team.Impostor)) return true;
        else if (seer.Is(Custom_Team.Impostor) && target.GetCustomRole().IsGhostRole() && target.GetCustomRole().IsImpostor()) return true;
        else if (target.GetRoleClass().KnowRoleTarget(seer, target)) return true;
        else if (seer.GetRoleClass().KnowRoleTarget(seer, target)) return true;
        else if (Solsticer.OtherKnowSolsticer(target)) return true;
        else if (Overseer.IsRevealedPlayer(seer, target) && !target.Is(CustomRoles.Trickster)) return true;
        else if (Gravestone.EveryoneKnowRole(target)) return true;
        else if (Mimic.CanSeeDeadRoles(seer, target)) return true;
        else if (Workaholic.OthersKnowWorka(target)) return true;
        else if (Jackal.JackalKnowRole(seer, target)) return true;
        else if (Cultist.KnowRole(seer, target)) return true;
        else if (Infectious.KnowRole(seer, target)) return true;
        else if (Virus.KnowRole(seer, target)) return true;


        else return false;
    }
    public static bool ShowSubRoleTarget(this PlayerControl seer, PlayerControl target, CustomRoles subRole = CustomRoles.NotAssigned)
    {
        if (seer == null) return false;
        if (target == null) target = seer;

        if (seer.PlayerId == target.PlayerId) return true;
        else if (seer.Is(CustomRoles.GM) || target.Is(CustomRoles.GM) || seer.Is(CustomRoles.God) || (seer.IsHost() && Main.GodMode.Value)) return true;
        else if (Main.VisibleTasksCount && !seer.IsAlive() && Options.GhostCanSeeOtherRoles.GetBool()) return true;
        else if (Options.ImpsCanSeeEachOthersAddOns.GetBool() && seer.Is(Custom_Team.Impostor) && target.Is(Custom_Team.Impostor) && !subRole.IsBetrayalAddon()) return true;
        else if (Options.ApocCanSeeEachOthersAddOns.GetBool() && seer.IsNeutralApocalypse() && target.IsNeutralApocalypse() && !subRole.IsBetrayalAddon()) return true;

        else if ((subRole is CustomRoles.Madmate
                or CustomRoles.Sidekick
                or CustomRoles.Recruit
                or CustomRoles.Admired
                or CustomRoles.Charmed
                or CustomRoles.Infected
                or CustomRoles.Contagious
                or CustomRoles.Egoist) 
            && KnowSubRoleTarget(seer, target))
            return true;

        
        return false;
    }
    public static bool KnowSubRoleTarget(PlayerControl seer, PlayerControl target)
    {
        //if (seer.GetRoleClass().KnowRoleTarget(seer, target)) return true;
        
        if (seer.Is(Custom_Team.Impostor))
        {
            // Imp know Madmate
            if (target.Is(CustomRoles.Madmate) && Madmate.ImpKnowWhosMadmate.GetBool())
                return true;

            // Ego-Imp know other Ego-Imp
            else if (seer.Is(CustomRoles.Egoist) && target.Is(CustomRoles.Egoist) && Egoist.ImpEgoistVisibalToAllies.GetBool())
                return true;
        }
        else if (Admirer.HasEnabled && Admirer.CheckKnowRoleTarget(seer, target)) return true;
        else if (Cultist.HasEnabled && Cultist.KnowRole(seer, target)) return true;
        else if (Infectious.HasEnabled && Infectious.KnowRole(seer, target)) return true;
        else if (Virus.HasEnabled && Virus.KnowRole(seer, target)) return true;
        else if (Jackal.HasEnabled)
        {
            if (seer.Is(CustomRoles.Jackal) || seer.Is(CustomRoles.Recruit))
                return target.Is(CustomRoles.Sidekick) || target.Is(CustomRoles.Recruit);

            else if (seer.Is(CustomRoles.Sidekick))
                return target.Is(CustomRoles.Recruit) || target.Is(CustomRoles.Sidekick);
        }

        return false;
    }

    public static bool CanBeTeleported(this PlayerControl player)
    {
        if (player.Data == null // Check if PlayerData is not null
            || Main.MeetingIsStarted
            // Check target status
            || !player.IsAlive()
            || player.inVent
            || player.walkingToVent
            || player.inMovingPlat // Moving Platform on Airhip and Zipline on Fungle
            || player.MyPhysics.Animations.IsPlayingEnterVentAnimation()
            || player.onLadder || player.MyPhysics.Animations.IsPlayingAnyLadderAnimation()
            || Pelican.IsEaten(player.PlayerId))
        {
            return false;
        }
        return true;
    }

    public static Vector2 GetCustomPosition(this PlayerControl player) => new(player.transform.position.x, player.transform.position.y);

    public static Vector2 GetBlackRoomPosition()
    {
        return Utils.GetActiveMapId() switch
        {
            0 => new Vector2(-27f, 3.3f), // The Skeld
            1 => new Vector2(-11.4f, 8.2f), // MIRA HQ
            2 => new Vector2(42.6f, -19.9f), // Polus
            3 => new Vector2(27f, 3.3f), // dlekS ehT
            4 => new Vector2(-16.8f, -6.2f), // Airship
            5 => new Vector2(10.2f, 18.1f), // The Fungle
            _ => throw new NotImplementedException(),
        };
    }
    public static int GetPlayerVentId(this PlayerControl player)
    {
        if (!(ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Ventilation, out var systemType) &&
              systemType.TryCast<VentilationSystem>() is VentilationSystem ventilationSystem))
            return 99;

        return ventilationSystem.PlayersInsideVents.TryGetValue(player.PlayerId, out var playerIdVentId) ? playerIdVentId : 99;
    }
    public static string GetRoleInfo(this PlayerControl player, bool InfoLong = false)
    {
        var role = player.GetCustomRole();
        if (role is CustomRoles.Crewmate or CustomRoles.Impostor)
            InfoLong = false;

        var text = role.ToString();

        var Prefix = "";
        if (!InfoLong && role == CustomRoles.Nemesis)
            Prefix = Nemesis.CheckCanUseKillButton() ? "After" : "Before";
            
        var Info = (role.IsVanilla() ? "Blurb" : "Info");
        return !InfoLong ? GetString($"{Prefix}{text}{Info}") : role.GetInfoLong();
    }
    public static void SetRealKiller(this PlayerControl target, PlayerControl killer, bool NotOverRide = false)
    {
        if (target == null)
        {
            Logger.Info("target=null", "SetRealKiller");
            return;
        }
        var State = Main.PlayerStates[target.PlayerId];
        if (State.RealKiller.Item1 != DateTime.MinValue && NotOverRide) return; //既に値がある場合上書きしない
        byte killerId = killer == null ? byte.MaxValue : killer.PlayerId;
        RPC.SetRealKiller(target.PlayerId, killerId);
    }
    public static PlayerControl GetRealKiller(this PlayerControl target)
    {
        var killerId = Main.PlayerStates[target.Data.PlayerId].GetRealKiller();
        return killerId == byte.MaxValue ? null : killerId.GetPlayer();
    }
    public static PlayerControl GetRealKiller(this PlayerControl target, out CustomRoles killerRole)
    {
        var killerId = Main.PlayerStates[target.Data.PlayerId].GetRealKiller();
        killerRole = Main.PlayerStates[target.Data.PlayerId].RoleofKiller;
        return killerId == byte.MaxValue ? null : killerId.GetPlayer();
    }
    public static PlayerControl GetRealKillerById(this byte targetId)
    {
        var killerId = Main.PlayerStates[targetId].GetRealKiller();
        return killerId == byte.MaxValue ? null : killerId.GetPlayer();
    }
    public static PlainShipRoom GetPlainShipRoom(this PlayerControl pc)
    {
        if (!pc.IsAlive() || Pelican.IsEaten(pc.PlayerId)) return null;
        var Rooms = ShipStatus.Instance.AllRooms.ToArray();
        if (Rooms == null) return null;
        foreach (var room in Rooms)
        {
            if (!room.roomArea) continue;
            if (pc.Collider.IsTouching(room.roomArea))
                return room;
        }
        return null;
    }

    /// <summary>
    /// Make sure to call PlayerState.Deathreason and not Vanilla Deathreason
    /// </summary>
    public static void SetDeathReason(this PlayerControl target, PlayerState.DeathReason reason) => target.PlayerId.SetDeathReason(reason);
    public static void SetDeathReason(this byte targetId, PlayerState.DeathReason reason)
    {
        Main.PlayerStates[targetId].deathReason = reason;
    }

    public static bool Is(this PlayerControl target, CustomRoles role) =>
        role > CustomRoles.NotAssigned ? target.GetCustomSubRoles().Contains(role) : target.GetCustomRole() == role;
    public static bool Is(this PlayerControl target, Custom_Team type) { return target.GetCustomRole().GetCustomRoleTeam() == type; }
    public static bool Is(this PlayerControl target, RoleTypes type) { return target.GetCustomRole().GetRoleTypes() == type; }
    public static bool Is(this PlayerControl target, CountTypes type) { return target.GetCountTypes() == type; }
    public static bool IsAnySubRole(this PlayerControl target, Func<CustomRoles, bool> predicate) => target != null && target.GetCustomSubRoles().Any() && target.GetCustomSubRoles().Any(predicate);

    public static bool IsAlive(this PlayerControl target)
    {
        //In lobby all is alive
        if (GameStates.IsLobby && !GameStates.IsInGame)
        {
            return true;
        }
        //if target is null, it is not alive
        if (target == null)
        {
            return false;
        }

        //if the target status is alive
        return !Main.PlayerStates.TryGetValue(target.PlayerId, out var playerState) || !playerState.IsDead;
    }
    public static bool IsDisconnected(this PlayerControl target)
    {
        //In lobby all not disconnected
        if (GameStates.IsLobby && !GameStates.IsInGame)
        {
            return false;
        }
        //if target is null, is disconnected
        if (target == null)
        {
            return true;
        }

        //if the target status is disconnected
        return !Main.PlayerStates.TryGetValue(target.PlayerId, out var playerState) || playerState.Disconnected;
    }
    public static bool IsExiled(this PlayerControl target)
    {
        return GameStates.InGame || (target != null && (Main.PlayerStates[target.PlayerId].deathReason == PlayerState.DeathReason.Vote));
    }
    ///<summary>Is the player currently protected</summary>
    public static bool IsProtected(this PlayerControl self) => self.protectedByGuardianId > -1;

    public const MurderResultFlags ResultFlags = MurderResultFlags.Succeeded; //No need for DecisonByHost
}
