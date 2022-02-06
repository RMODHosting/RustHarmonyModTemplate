using Harmony;
using Steamworks;
using UnityEngine;

namespace FuckOxideISetMyOwnNamespaces
{
    static class Bootstrap
    {
        /// <summary>
        /// Test patch into the end of ServerMgr
        /// </summary>
        [HarmonyPatch(typeof(ServerMgr), "Update")]
        public class ServerUpdateHook
        {
            [HarmonyPostfix]
            public static void Postfix(ServerMgr __instance)
            {
                Debug.Log("Spam in console! - Here's a private string: " + ServerMgr.Instance._AssemblyHash);
            }
        } 
        
        /// <summary>
        /// Add modded tag so we don't get banned.
        /// todo: make this mixin not shit
        /// </summary>
        [HarmonyPatch(typeof(ServerMgr), "UpdateServerInformation")]
        public class UpdateServerInformationHook
        {
            [HarmonyPostfix]
            public static void Postfix(ServerMgr __instance)
            {
                SteamServer.GameTags += ",modded";
            }
        }
    }
}
