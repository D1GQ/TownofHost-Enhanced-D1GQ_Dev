using System;
using UnityEngine;
using static TOHE.Utils;
using static TOHE.Options;


namespace TOHE;

//From : https://github.com/ykundesu/SuperNewRoles/blob/master/SuperNewRoles/Mode/SuperHostRoles/BlockTool.cs
class DisableDevice
{
    private static bool DoDisable => DisableDevices.GetBool();
    private static readonly HashSet<byte> DesyncComms = [];
    private static int frame = 0;
    public static readonly Dictionary<string, Vector2> DevicePos = new()
    {
        ["SkeldAdmin"] = new Vector2(3.48f, -8.62f),
        ["SkeldCamera"] = new Vector2(-13.06f, -2.45f),
        ["MiraHQAdmin"] = new Vector2(21.02f, 19.09f),
        ["MiraHQDoorLog"] = new Vector2(16.22f, 5.82f),
        ["PolusLeftAdmin"] = new Vector2(22.80f, -21.52f),
        ["PolusRightAdmin"] = new Vector2(24.66f, -21.52f),
        ["PolusCamera"] = new Vector2(2.96f, -12.74f),
        ["PolusVital"] = new Vector2(26.70f, -15.94f),
        ["DleksAdmin"] = new Vector2(-3.48f, -8.62f),
        ["DleksCamera"] = new Vector2(13.06f, -2.45f),
        ["AirshipCockpitAdmin"] = new Vector2(-22.32f, 0.91f),
        ["AirshipRecordsAdmin"] = new Vector2(19.89f, 12.60f),
        ["AirshipCamera"] = new Vector2(8.10f, -9.63f),
        ["AirshipVital"] = new Vector2(25.24f, -7.94f),
        ["FungleCamera"] = new Vector2(6.20f, 0.10f),
        ["FungleVital"] = new Vector2(-2.50f, -9.80f)
    };
    public static float UsableDistance(MapNames Map)
    {
        return Map switch
        {
            MapNames.Skeld => 1.8f,
            MapNames.Mira => 2.4f,
            MapNames.Polus => 1.8f,
            MapNames.Dleks => 1.5f,
            MapNames.Airship => 1.8f,
            MapNames.Fungle => 1.8f,
            _ => 0.0f
        };
    }
    public static void FixedUpdate()
    {
        frame = frame == 3 ? 0 : ++frame;
        if (frame != 0) return;

        if (!DoDisable) return;

        foreach (var pc in Main.AllPlayerControls)
        {
            try
            {
                if (pc.IsModded()) continue;

                bool doComms = false;
                Vector2 PlayerPos = pc.transform.position;
                bool ignore = (DisableDevicesIgnoreImpostors.GetBool() && pc.Is(Custom_Team.Impostor)) ||
                        (DisableDevicesIgnoreNeutrals.GetBool() && pc.Is(Custom_Team.Neutral)) ||
                        (DisableDevicesIgnoreCrewmates.GetBool() && pc.Is(Custom_Team.Crewmate)) ||
                        (DisableDevicesIgnoreCoven.GetBool() && pc.Is(Custom_Team.Coven)) ||
                        (DisableDevicesIgnoreAfterAnyoneDied.GetBool() && GameStates.AlreadyDied);

                var mapId = GetActiveMapId();
                var mapName = (MapNames)mapId;

                if (pc.IsAlive() && !IsActive(SystemTypes.Comms))
                {
                    switch (mapId)
                    {
                        case 0:
                            if (DisableSkeldAdmin.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["SkeldAdmin"]) <= UsableDistance(mapName);
                            if (DisableSkeldCamera.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["SkeldCamera"]) <= UsableDistance(mapName);
                            break;
                        case 1:
                            if (DisableMiraHQAdmin.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["MiraHQAdmin"]) <= UsableDistance(mapName);
                            if (DisableMiraHQDoorLog.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["MiraHQDoorLog"]) <= UsableDistance(mapName);
                            break;
                        case 2:
                            if (DisablePolusAdmin.GetBool())
                            {
                                doComms |= GetDistance(PlayerPos, DevicePos["PolusLeftAdmin"]) <= UsableDistance(mapName);
                                doComms |= GetDistance(PlayerPos, DevicePos["PolusRightAdmin"]) <= UsableDistance(mapName);
                            }
                            if (DisablePolusCamera.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["PolusCamera"]) <= UsableDistance(mapName);
                            if (DisablePolusVital.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["PolusVital"]) <= UsableDistance(mapName);
                            break;
                        case 3:
                            if (DisableSkeldAdmin.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["DleksAdmin"]) <= UsableDistance(mapName);
                            if (DisableSkeldCamera.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["DleksCamera"]) <= UsableDistance(mapName);
                            break;
                        case 4:
                            if (DisableAirshipCockpitAdmin.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["AirshipCockpitAdmin"]) <= UsableDistance(mapName);
                            if (DisableAirshipRecordsAdmin.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["AirshipRecordsAdmin"]) <= UsableDistance(mapName);
                            if (DisableAirshipCamera.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["AirshipCamera"]) <= UsableDistance(mapName);
                            if (DisableAirshipVital.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["AirshipVital"]) <= UsableDistance(mapName);
                            break;
                        case 5:
                            if (DisableFungleBinoculars.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["FungleCamera"]) <= UsableDistance(mapName);
                            if (DisableFungleVital.GetBool())
                                doComms |= GetDistance(PlayerPos, DevicePos["FungleVital"]) <= UsableDistance(mapName);
                            break;
                    }
                }
                doComms &= !ignore;
                if (doComms && !pc.inVent)
                {
                    DesyncComms.Add(pc.PlayerId);
                    pc.RpcDesyncUpdateSystem(SystemTypes.Comms, 128);
                }
                else if (!IsActive(SystemTypes.Comms) && DesyncComms.Contains(pc.PlayerId))
                {
                    DesyncComms.Remove(pc.PlayerId);
                    pc.RpcDesyncUpdateSystem(SystemTypes.Comms, 16);

                    if (mapId is 1 or 5) // Mira HQ or The Fungle
                        pc.RpcDesyncUpdateSystem(SystemTypes.Comms, 17);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "DisableDevice");
            }
        }
    }
}
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
public class RemoveDisableDevicesPatch
{
    public static void Postfix()
    {
        if (!DisableDevices.GetBool()) return;
        UpdateDisableDevices();
    }

    public static void UpdateDisableDevices()
    {
        var player = PlayerControl.LocalPlayer;
        bool ignore = player.Is(CustomRoles.GM) ||
            !player.IsAlive() ||
            (DisableDevicesIgnoreImpostors.GetBool() && player.Is(Custom_Team.Impostor)) ||
            (DisableDevicesIgnoreNeutrals.GetBool() && player.Is(Custom_Team.Neutral)) ||
            (DisableDevicesIgnoreCrewmates.GetBool() && player.Is(Custom_Team.Crewmate)) ||
            (DisableDevicesIgnoreCoven.GetBool() && player.Is(Custom_Team.Coven)) ||
            (DisableDevicesIgnoreAfterAnyoneDied.GetBool() && GameStates.AlreadyDied);

        var admins = UnityEngine.Object.FindObjectsOfType<MapConsole>(true);
        var consoles = UnityEngine.Object.FindObjectsOfType<SystemConsole>(true);
        if (admins == null || consoles == null) return;

        switch (GetActiveMapId())
        {
            case 0:
            case 3:
                if (DisableSkeldAdmin.GetBool())
                    admins[0].gameObject.GetComponent<CircleCollider2D>().enabled = ignore;
                if (DisableSkeldCamera.GetBool())
                    consoles.DoIf(x => x.name == "SurvConsole", x => x.gameObject.GetComponent<PolygonCollider2D>().enabled = ignore);
                break;
            case 1:
                if (DisableMiraHQAdmin.GetBool())
                    admins[0].gameObject.GetComponent<CircleCollider2D>().enabled = ignore;
                if (DisableMiraHQDoorLog.GetBool())
                    consoles.DoIf(x => x.name == "SurvLogConsole", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                break;
            case 2:
                if (DisablePolusAdmin.GetBool())
                    admins.Do(x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                if (DisablePolusCamera.GetBool())
                    consoles.DoIf(x => x.name == "Surv_Panel", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                if (DisablePolusVital.GetBool())
                    consoles.DoIf(x => x.name == "panel_vitals", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                break;
            case 4:
                admins.Do(x =>
                {
                    if ((DisableAirshipCockpitAdmin.GetBool() && x.name == "panel_cockpit_map") ||
                        (DisableAirshipRecordsAdmin.GetBool() && x.name == "records_admin_map"))
                        x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore;
                });
                if (DisableAirshipCamera.GetBool())
                    consoles.DoIf(x => x.name == "task_cams", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                if (DisableAirshipVital.GetBool())
                    consoles.DoIf(x => x.name == "panel_vitals", x => x.gameObject.GetComponent<CircleCollider2D>().enabled = ignore);
                break;
            case 5:
                if (DisableFungleBinoculars.GetBool())
                    consoles.DoIf(x => x.name == "BinocularsSecurityConsole", x => x.gameObject.GetComponent<PolygonCollider2D>().enabled = ignore);
                if (DisableFungleVital.GetBool())
                    consoles.DoIf(x => x.name == "VitalsConsole", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                break;
        }
    }
}
