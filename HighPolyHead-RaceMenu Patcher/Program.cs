using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using System.Threading.Tasks;
using Noggog;

namespace HighPolyHeadUpdateRaces
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "High Poly Head - RaceMenu Patcher.esp")
                .Run(args);
        }
        
        private static readonly ModKey ModKey = ModKey.FromNameAndExtension("High Poly Head.esm");



        private static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!state.LoadOrder.ContainsKey(ModKey))
            {
                throw new Exception("You need High Poly Head mod installed for this patch to do anything.");
            }
            
            Console.WriteLine("Running Test!");

            var hphParts = new HashSet<IFormLinkGetter<IHeadPartGetter>>();
            foreach (var hphHeadPart in state.LoadOrder.PriorityOrder.OnlyEnabled().HeadPart().WinningOverrides())
            {
                if (hphHeadPart.EditorID != null && hphHeadPart.EditorID.StartsWith("00KLH_"))
                {
                    hphParts.Add(hphHeadPart.ToLinkGetter());
                }
            }

            foreach (var vanillaHeadPart in state.LoadOrder.PriorityOrder.HeadPart().WinningOverrides())
            {
                if (vanillaHeadPart.EditorID != null && !vanillaHeadPart.EditorID.StartsWith("00KLH_"))
                {
                    foreach (var hphHeadPart in hphParts)
                    {
                        if (hphHeadPart.TryResolve(state.LinkCache, out var hph) && hph.EditorID != null && vanillaHeadPart.EditorID != null && hph.EditorID.EndsWith(vanillaHeadPart.EditorID))
                        {
                            if (state.PatchMod.MasterReferences.Count() >= 253)
                            {
                                Console.WriteLine($"Cannot add {vanillaHeadPart.FormKey.ModKey} as a master, as the patch has already reached the 254 master limit. Aborting to prevent a corrupt plugin.");
                                continue;
                            }
                            IHeadPart gimmeHead = state.PatchMod.HeadParts.GetOrAddAsOverride(vanillaHeadPart);
                            gimmeHead.Flags &= ~HeadPart.Flag.Playable;
                            break;
                        }
                    }
                }
            }
            


            foreach(var npcPreset in state.LoadOrder.PriorityOrder.OnlyEnabled().Npc().WinningOverrides())
            {
                var npcOverride = npcPreset.DeepCopy();
                var changed = false;

                for (var i = 0; i < npcOverride.HeadParts.Count; i++)
                {
                    var part = npcOverride.HeadParts[i];
                    if (part.TryResolve(state.LinkCache, out var headPart))
                    {
                        foreach (var hphHeadPart in hphParts)
                        {
                            if (hphHeadPart.TryResolve(state.LinkCache, out var hph) && hph.EditorID != null && headPart.EditorID != null && hph.EditorID.EndsWith(headPart.EditorID))
                            {
                                npcOverride.HeadParts[i] = hphHeadPart;
                                changed = true;
                                break;
                            }
                        }
                    }
                }

                if (changed)
                {
                    if (state.PatchMod.MasterReferences.Count() >= 253)
                    {
                        Console.WriteLine($"Cannot add {npcPreset.FormKey.ModKey} as a master, as the patch has already reached the 254 master limit. Aborting to prevent a corrupt plugin.");
                        continue;
                    }
                    state.PatchMod.Npcs.Set(npcOverride);
                }
            }
        }
    }
}