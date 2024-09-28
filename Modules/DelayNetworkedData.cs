﻿using Hazel;
using InnerNet;
using System;
using UnityEngine;

namespace TOHE.Modules.DelayNetworkDataSpawn;

[HarmonyPatch(typeof(InnerNetClient))]
public class InnerNetClientPatch
{
    //public static List<MessageWriter> DelayedSpawnPlayers = [];

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SendInitialData))]
    [HarmonyPrefix]
    public static bool SendInitialDataPrefix(InnerNetClient __instance, int clientId)
    {
        if (!Constants.IsVersionModded() || GameStates.IsInGame || __instance.NetworkMode != NetworkModes.OnlineGame) return true;
        // We make sure other stuffs like playercontrol and Lobby behavior is spawned properly
        // Then we spawn networked data for new clients
        MessageWriter messageWriter = MessageWriter.Get(SendOption.Reliable);
        messageWriter.StartMessage(6);
        messageWriter.Write(__instance.GameId);
        messageWriter.WritePacked(clientId);
        Il2CppSystem.Collections.Generic.List<InnerNetObject> obj = __instance.allObjects;
        lock (obj)
        {
            HashSet<GameObject> hashSet = [];
            for (int i = 0; i < __instance.allObjects.Count; i++)
            {
                InnerNetObject innerNetObject = __instance.allObjects[i];
                if (innerNetObject && (innerNetObject.OwnerId != -4 || __instance.AmModdedHost) && hashSet.Add(innerNetObject.gameObject))
                {
                    GameManager gameManager = innerNetObject as GameManager;
                    if (gameManager != null)
                    {
                        __instance.SendGameManager(clientId, gameManager);
                    }
                    else
                    {
                        if (innerNetObject is not NetworkedPlayerInfo)
                            __instance.WriteSpawnMessage(innerNetObject, innerNetObject.OwnerId, innerNetObject.SpawnFlags, messageWriter);
                    }
                }
            }
            messageWriter.EndMessage();
            // Logger.Info($"send first data to {clientId}, size is {messageWriter.Length}", "SendInitialDataPrefix");
            __instance.SendOrDisconnect(messageWriter);
            messageWriter.Recycle();
        }
        DelayInitialSpawnPlayerInfo(__instance, clientId);
        return false;
    }

    // InnerSloth vanilla officials send PlayerInfo in spilt reliable packets
    private static void DelayInitialSpawnPlayerInfo(InnerNetClient __instance, int clientId)
    {
        List<NetworkedPlayerInfo> players = GameData.Instance.AllPlayers.ToArray().ToList();

        foreach (var player in players)
        {
            if (player != null && player.ClientId != clientId && !player.Disconnected)
            {
                MessageWriter messageWriter = MessageWriter.Get(SendOption.Reliable);
                messageWriter.StartMessage(6);
                messageWriter.Write(__instance.GameId);
                messageWriter.WritePacked(clientId);

                __instance.WriteSpawnMessage(player, player.OwnerId, player.SpawnFlags, messageWriter);
                messageWriter.EndMessage();

                __instance.SendOrDisconnect(messageWriter);
                messageWriter.Recycle();
            }
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SendAllStreamedObjects))]
    [HarmonyPrefix]
    public static bool SendAllStreamedObjectsPrefix(InnerNetClient __instance, ref bool __result)
    {
        if (!Constants.IsVersionModded() || __instance.NetworkMode != NetworkModes.OnlineGame) return true;
        // Bypass all NetworkedData here.
        __result = false;
        Il2CppSystem.Collections.Generic.List<InnerNetObject> obj = __instance.allObjects;
        lock (obj)
        {
            for (int i = 0; i < __instance.allObjects.Count; i++)
            {
                InnerNetObject innerNetObject = __instance.allObjects[i];
                if (innerNetObject && innerNetObject is not NetworkedPlayerInfo && innerNetObject.IsDirty && (innerNetObject.AmOwner || (innerNetObject.OwnerId == -2 && __instance.AmHost)))
                {
                    MessageWriter messageWriter = __instance.Streams[(int)innerNetObject.sendMode];
                    messageWriter.StartMessage(1);
                    messageWriter.WritePacked(innerNetObject.NetId);
                    try
                    {
                        if (innerNetObject.Serialize(messageWriter, false))
                        {
                            messageWriter.EndMessage();
                        }
                        else
                        {
                            messageWriter.CancelMessage();
                        }
                        if (innerNetObject.Chunked && innerNetObject.IsDirty)
                        {
                            __result = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Exception(ex, "SendAllStreamedObjectsPrefix");
                        messageWriter.CancelMessage();
                    }
                }
            }
        }
        for (int j = 0; j < __instance.Streams.Length; j++)
        {
            MessageWriter messageWriter2 = __instance.Streams[j];
            if (messageWriter2.HasBytes(7))
            {
                messageWriter2.EndMessage();
                __instance.SendOrDisconnect(messageWriter2);
                messageWriter2.Clear((SendOption)j);
                messageWriter2.StartMessage(5);
                messageWriter2.Write(__instance.GameId);
            }
        }
        return false;
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.Spawn))]
    [HarmonyPrefix]
    public static bool SpawnPrefix(InnerNetClient __instance, InnerNetObject netObjParent, int ownerId = -2, SpawnFlags flags = SpawnFlags.None)
    {
        if (!Constants.IsVersionModded() || __instance.NetworkMode != NetworkModes.OnlineGame) return true;

        if (__instance.AmHost)
        {
            ownerId = ((ownerId == -3) ? __instance.ClientId : ownerId);
            MessageWriter msg = MessageWriter.Get(SendOption.Reliable);
            msg.StartMessage(5);
            msg.Write(__instance.GameId);
            __instance.WriteSpawnMessage(netObjParent, ownerId, flags, msg);
            msg.EndMessage();

            //For unknow reason delaying playerinfo spawn here will make beans much easier to appear.
            //Especially when spawning lots of players on game end
            //Leaving these codes for further use.
            /*
            if (netObjParent is NetworkedPlayerInfo)
            {
                DelayedSpawnPlayers.Add(msg);
                return false;
            }
            */

            if (netObjParent is NetworkedPlayerInfo playerinfo)
            {
                _ = new LateTask(() =>
                {
                    if (playerinfo != null && AmongUsClient.Instance.AmConnected)
                    {
                        var client = AmongUsClient.Instance.GetClient(playerinfo.ClientId);
                        if (client != null && !client.IsDisconnected())
                        {
                            if (playerinfo.IsIncomplete)
                            {
                                Logger.Info($"Disconnecting Client [{client.Id}]{client.PlayerName} {client.FriendCode} for playerinfo timeout", "DelayedNetworkedData");
                                AmongUsClient.Instance.SendLateRejection(client.Id, DisconnectReasons.ClientTimeout);
                                __instance.OnPlayerLeft(client, DisconnectReasons.ClientTimeout);
                            }
                        }
                    }
                }, 5f, "PlayerInfo Green Bean Kick", false);
            }

            if (netObjParent is PlayerControl player)
            {
                _ = new LateTask(() =>
                {
                    if (player != null && !player.notRealPlayer && !player.isDummy && AmongUsClient.Instance.AmConnected)
                    {
                        var client = AmongUsClient.Instance.GetClient(player.OwnerId);
                        if (client != null && !client.IsDisconnected())
                        {
                            if (player.Data == null  || player.Data.IsIncomplete)
                            {
                                Logger.Info($"Disconnecting Client [{client.Id}]{client.PlayerName} {client.FriendCode} for playercontrol timeout", "DelayedNetworkedData");
                                AmongUsClient.Instance.SendLateRejection(client.Id, DisconnectReasons.ClientTimeout);
                                __instance.OnPlayerLeft(client, DisconnectReasons.ClientTimeout);
                            }
                        }
                    }
                }, 5.5f, "PlayerControl Green Bean Kick", false);
            }

            AmongUsClient.Instance.SendOrDisconnect(msg);
        }

        if (__instance.AmClient)
        {
            Debug.LogError("Tried to spawn while not host:" + (netObjParent?.ToString()));
        }
        return false;
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.FixedUpdate))]
    [HarmonyPostfix]
    public static void FixedUpdatePostfix(InnerNetClient __instance)
    {
        // Just send with None calls. Who cares?
        if (!Constants.IsVersionModded() || GameStates.IsInGame || __instance == null || __instance.NetworkMode != NetworkModes.OnlineGame) return;
        if (!__instance.AmHost || __instance.Streams == null) return;

        /*
        var delayedPlayers = DelayedSpawnPlayers.Take(2);
        foreach (var msg in delayedPlayers)
        {
            AmongUsClient.Instance.SendOrDisconnect(msg);
            msg.Recycle();
            DelayedSpawnPlayers.Remove(msg);
        }

        if (DelayedSpawnPlayers.Count >= 2) return;
        */

        // We are serializing 2 Networked playerinfo maxium per fixed update
        var players = GameData.Instance.AllPlayers.ToArray()
            .Where(x => x.IsDirty)
            .Take(2)
            .ToList();

        if (players != null)
        {
            foreach (var player in players)
            {
                MessageWriter messageWriter = MessageWriter.Get(SendOption.Reliable);
                messageWriter.StartMessage(5);
                messageWriter.Write(__instance.GameId);
                messageWriter.StartMessage(1);
                messageWriter.WritePacked(player.NetId);
                try
                {
                    if (player.Serialize(messageWriter, false))
                    {
                        messageWriter.EndMessage();
                    }
                    else
                    {
                        messageWriter.CancelMessage();
                        player.ClearDirtyBits();
                        continue;
                    }
                    messageWriter.EndMessage();
                    __instance.SendOrDisconnect(messageWriter);
                    messageWriter.Recycle();
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, "FixedUpdatePostfix");
                    messageWriter.CancelMessage();
                    player.ClearDirtyBits();
                    continue;
                }
            }
        }
    }
}

[HarmonyPatch(typeof(GameData), nameof(GameData.DirtyAllData))]
internal class DirtyAllDataPatch
{
    // Currently this function only occurs in CreatePlayer
    // It's believed to lag host, delay the playercontrol spawn mesasge & blackout new client
    // & send huge packets to all clients & completely no need to run
    // Temporarily disable it until Innersloth get a better fix.
    public static bool Prefix()
    {
        return false;
    }
}
