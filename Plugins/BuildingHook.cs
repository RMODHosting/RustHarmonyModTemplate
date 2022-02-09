using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Harmony;
using Radium.Core;
using Steamworks;
using UnityEngine;

namespace FuckOxideISetMyOwnNamespaces
{
    static class BuildingHook
    {
        // target the method with a harmony patch
        [HarmonyPatch(typeof(Planner), nameof(Planner.DoBuild), typeof(Construction.Target), typeof(Construction))]
        public class OnEntityBuiltPatch
        {
            // use a transpiler for total control
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator ilgen, MethodBase originalMethod)
            {
                // Make a new hook builder helper class
                // this functions like a record player that can move back and forth and add/remove instructions
                return new HookBuilder(instructions, originalMethod, ilgen)
                    // move the playhead to before the given method, Planner.GetDeployable
                    // this is an instance method, so it's called on this, meaning the defining type is Planner
                    .MoveBeforeMethod(typeof(Planner), nameof(Planner.GetDeployable)) 
                    // insert a direct call to our OnEntityBuilt function defined below this connects Planner.DoBuild
                    // directly to BuildingHook, no middleman, literally a "call" opcode with a function pointer to our handler
                    .InsertDirectCall(
                        AccessTools.Method(typeof(BuildingHook), nameof(OnEntityBuilt)), 
                        new ArgThis(), 
                        new ArgLocal(typeof(GameObject))
                        );
            
                // hook builder extends IEnumerable<CodeInstruction>, so you can just return a hook builder
                // and it will do all its logic when Harmony parses it.
            } 
        }


        // called by Rust code using the InsertDirectCall helper
        public static void OnEntityBuilt(Planner planner, GameObject go)
        {
            Debug.Log("OnEntityBuilt - " + go.transform.position);
        }
        
        
    }
}