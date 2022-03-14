﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

/* Prevent occasional kraken events on flight scene loads and possiblty other cases
 * where a part is created after a scene load.
 *
 * Technical details :
 *
 * The Part.Start() method is implemented as a coroutine that yield on Update() :
 * - immediate : setup part mass and thermal stats, apply physics significance by
 *   making non-significant parts GOs child of the GO of the part they are attached to,
 *   call OnStart on modules.
 * - after first yield : setup rigidbodies and collisions matrix
 * - after second yield : setup joints
 *
 * Due to Time.maximumDeltaTime being greater than Time.fixedDeltaTime, there is possibility that
 * several Update() cycles can happen back-to-back before any FixedUpdate() happen, especially
 * if the maximum delta time has been increased from the default 0.03 by the user (main menu setting)
 * The exact path that lead to a kraken event when that happen isn't clear. It could simply be caused
 * by the the fact that rigidbodies exists and thus will start moving before joints are created, 
 * or it could be an interaction with another component (autostruts, docking ports...).
 *
 * There are 2 possible ways to fix this :
 * 1. Identify cases of Part.Start() being called, and enforce Update/FixedUpdate synchronization.
 *    This can be acheived by setting Time.maximumDeltaTime to Time.fixedDeltaTime. However, for that
 *    to take effect, it must be done on frame in advance. Unless we force it all the time (which isn't
 *    desirable for the user experience), this de facto exclude the possibility of using that workaround
 *    when reacting to events, but it's possible to implement it for scene loads.
 * 2. Patch Part.Start() to yield on FixedUpdate() instead of Update(). This cover all cases and is
 *    conceptually the "true" fix. However, this can potentially have side effects for other components
 *    that are also using Update() yielding initialization coroutines. From my limited testing, there
 *    is no such issue in practice, but it's hard to cover all cases, since there are a lot of potential
 *    combinations in the first frames FixedUpdate/Update call order, and it's impossible to force Unity
 *    to behave predictably for testing.
 *
 * Patch details :
 *
 * We are using option 2 here, using a transpiler to edit the compiler generated Part.Start() enumerator
 * class, and more specifically its MoveNext() method. We search all calls to "yield return null" (yield
 * on Update), and replace them with a "yield return new WaitForFixedUpdate()" call.
 */

namespace KSPCommunityFixes
{
    public class PartStartStability : BasePatch
    {
        protected override Version VersionMin => new Version(1, 8, 0);

        private static Type partStartEnumerator;
        private static FieldInfo current;
        private static MethodBase waitForFixedUpdateCtor;

        protected override void ApplyPatches(ref List<PatchInfo> patches)
        {
            Type partType = typeof(Part);

            foreach (Type type in partType.GetNestedTypes(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (type.Name.StartsWith("<Start>", StringComparison.Ordinal))
                {
                    partStartEnumerator = type;
                    break;
                }
            }

            if (partStartEnumerator == null)
                throw new Exception("Part.Start : generated enumerator class not found");

            foreach (FieldInfo field in partStartEnumerator.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.Name.Contains("current"))
                {
                    current = field;
                    break;
                }
            }

            if (current == null)
                throw new Exception("Part.Start : `current` field not found in generated enumerator class");

            waitForFixedUpdateCtor = AccessTools.Constructor(typeof(WaitForFixedUpdate));

            patches.Add(new PatchInfo(
                PatchMethodType.Transpiler,
                AccessTools.Method(partStartEnumerator, "MoveNext"),
                this,
                nameof(Part_StartEnumerator_MoveNext_Transpiler)));
        }

        static IEnumerable<CodeInstruction> Part_StartEnumerator_MoveNext_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            for (int i = 1; i < code.Count - 1; i++)
            {
                if (code[i - 1].opcode == OpCodes.Ldnull && code[i].opcode == OpCodes.Stfld && (FieldInfo)code[i].operand == current)
                {
                    code[i - 1].opcode = OpCodes.Newobj;
                    code[i - 1].operand = waitForFixedUpdateCtor;
                }
            }

            return code;
        }
    }
}