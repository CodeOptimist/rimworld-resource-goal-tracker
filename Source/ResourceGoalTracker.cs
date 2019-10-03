﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony;
using HugsLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

//using HugsLib.Settings;

namespace ResourceGoalTracker
{
    class ResourceGoalTracker : ModBase
    {
        static Goal curGoal;
        public override string ModIdentifier => "COResourceGoalTracker";

        public override void DefsLoaded() {
            //SettingHandle<T> GetSettingHandle<T>(string settingName, T defaultValue) {
            //    return Settings.GetHandle(settingName, $"CORGT_{settingName}Setting_title".Translate(), $"CORGT_{settingName}Setting_description".Translate(), defaultValue);
            //}

            curGoal = Goal.Presets.reactorOnly;
        }

        class ResourceSettings : WorldComponent
        {
            static Dictionary<ThingDef, RecipeDef> thingsRecipes = new Dictionary<ThingDef, RecipeDef>();

            public ResourceSettings(World world) : base(world) {
            }

            public static RecipeDef RecipeFor(ThingDef thingDef) {
                if (thingsRecipes.TryGetValue(thingDef, out var recipe))
                    return recipe;
                recipe = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(r => r.products.Any(tc => tc.thingDef == thingDef));
                if (recipe != null)
                    thingsRecipes[thingDef] = recipe;
                return recipe;
            }

            public override void ExposeData() {
                Scribe_Collections.Look(ref thingsRecipes, "recipes", LookMode.Def, LookMode.Def);

                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                    if (thingsRecipes == null)
                        thingsRecipes = new Dictionary<ThingDef, RecipeDef>();
            }
        }

        class Goal
        {
            static readonly Dictionary<RecipeDef, List<ThingDefCountClass>> recipesIngredientCounts = new Dictionary<RecipeDef, List<ThingDefCountClass>>();
            readonly Action<Goal> CustomCounterTick;
            readonly Dictionary<ThingDef, int> parts;
            public Dictionary<ThingDef, int> resourceAmounts;

            Goal(Dictionary<ThingDef, int> parts, Action<Goal> customCounterTick = null) {
                this.parts = parts;
                CustomCounterTick = customCounterTick;
            }

            static List<ThingDefCountClass> IngredientCountsFor(RecipeDef recipe) {
                if (recipe == null) return null;
                if (recipesIngredientCounts.TryGetValue(recipe, out var ingredientCounts))
                    return ingredientCounts;

                ingredientCounts = new List<ThingDefCountClass>();
                recipesIngredientCounts[recipe] = ingredientCounts;
                ingredientCounts.AddRange(
                    from ingredientCount in recipe.ingredients
                    from thingDef in ingredientCount.filter.AllowedThingDefs
                    let count = ingredientCount.CountRequiredOfFor(thingDef, recipe)
                    select new ThingDefCountClass(thingDef, count));
                return ingredientCounts;
            }

            public void CounterTick() {
                CustomCounterTick?.Invoke(curGoal);
                UpdateResourceAmounts();
            }

            // todo? live update from trade menu pre-confirm
            void UpdateResourceAmounts() {
                var countedParts = CountAll(Find.CurrentMap, parts.Keys, false, true);
                var remainingParts = new Dictionary<ThingDef, int>();
                foreach (var part in parts)
                    remainingParts[part.Key] = Math.Max(part.Value - countedParts.TryGetValue(part.Key), 0);

                var costs = new Dictionary<ThingDef, int>();
                foreach (var part in remainingParts)
                foreach (var cost in part.Key.costList)
                    costs[cost.thingDef] = costs.TryGetValue(cost.thingDef) + part.Value * cost.count;

                // base on costs so we have ALL keys
                var deepCosts = new Dictionary<ThingDef, int>(costs);
                foreach (var cost in costs) {
                    var ingredientCounts = IngredientCountsFor(ResourceSettings.RecipeFor(cost.Key));
                    if (ingredientCounts == null) continue;
                    // deep counts of only remaining things
                    var costCount = Math.Max(cost.Value - Find.CurrentMap.resourceCounter.GetCount(cost.Key), 0);
                    foreach (var ingredientCount in ingredientCounts)
                        deepCosts[ingredientCount.thingDef] = costs.TryGetValue(ingredientCount.thingDef) + costCount * ingredientCount.count;
                }

                var remainingCosts = deepCosts.ToDictionary(cost => cost.Key, cost => Math.Max(cost.Value - Find.CurrentMap.resourceCounter.GetCount(cost.Key), 0));
                resourceAmounts = remainingCosts;
            }

            static Dictionary<ThingDef, int> CountAll(Map map, IEnumerable<ThingDef> thingDefs, bool includeEquipped, bool includeBuildings) {
                var result = new Dictionary<ThingDef, int>();
                foreach (var thingDef in thingDefs) {
                    int asResourceCount = 0, count = 0, minifiedCount = 0, equippedCount = 0, wornCount = 0, directlyHeldCount = 0;
                    if (thingDef.CountAsResource) {
                        asResourceCount = map.resourceCounter.GetCount(thingDef);
                    } else {
                        count = map.listerThings.ThingsOfDef(thingDef).Where(x => includeBuildings || !(x is Building)).Sum(x => x.stackCount);
                        minifiedCount = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).Cast<MinifiedThing>().Where(x => x.InnerThing.def == thingDef)
                            .Select(x => x.stackCount * x.InnerThing.stackCount).Sum();
                    }

                    var carriedCount = map.mapPawns.FreeColonistsSpawned.Where(x => x.carryTracker.CarriedThing?.def == thingDef).Select(x => x.carryTracker.CarriedThing)
                        .Sum(x => x.stackCount * (x is MinifiedThing minifiedThing ? minifiedThing.stackCount : 1));

                    if (includeEquipped) {
                        equippedCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.equipment.AllEquipmentListForReading).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        wornCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.apparel.WornApparel).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        directlyHeldCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.inventory.GetDirectlyHeldThings()).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                    }

                    var totalCount = asResourceCount + count + minifiedCount + carriedCount + equippedCount + wornCount + directlyHeldCount;
                    result.Add(thingDef, totalCount);
                }

                return result;
            }

            public static FloatMenu FloatMenu(ThingDef iconThingDef) {
                var options = MainFloatMenu();

                //todo list and choose recipe per thingdef
                var result = new FloatMenu(options);
                return result;
            }

            static List<FloatMenuOption> MainFloatMenu() {
                var result = new List<FloatMenuOption>();

                FloatMenuOption GoalFloatMenuOption(Goal goal, string label) {
                    return new FloatMenuOption(
                        (curGoal == goal ? "✔ " : "") + label, () => {
                            curGoal = goal;
                            curGoal.CounterTick(); // things need initialized before they are drawn
                        });
                }

                result.Add(GoalFloatMenuOption(Presets.reactorOnly, $"1 {ThingDefOf.Ship_Reactor.label}"));

                var casketCount = Presets.shipMinColonists.parts.TryGetValue(ThingDefOf.Ship_CryptosleepCasket);
                result.Add(GoalFloatMenuOption(Presets.shipMinColonists, casketCount > 0 ? $"ship minimum ({casketCount} {ThingDefOf.Ship_CryptosleepCasket.label})" : "ship"));

                if (casketCount > 0) {
                    result.Add(GoalFloatMenuOption(Presets.shipMapColonists, $"ship for map colonists ({Find.CurrentMap.mapPawns.FreeColonistsCount} {ThingDefOf.Ship_CryptosleepCasket.label})"));
                    var allColonistsCount = PawnsFinder.AllMapsCaravansAndTravelingTransportPods_Alive_FreeColonists.Count();
                    result.Add(GoalFloatMenuOption(Presets.shipAllColonists, $"ship for all colonists ({allColonistsCount} {ThingDefOf.Ship_CryptosleepCasket.label})"));
                }

                return result;
            }

            public static class Presets
            {
                public static readonly Goal reactorOnly = new Goal(new Dictionary<ThingDef, int> {{ThingDefOf.Ship_Reactor, 1}});
                public static readonly Goal shipMinColonists = new Goal(new Dictionary<ThingDef, int>(ShipUtility.RequiredParts()));

                public static readonly Goal shipMapColonists = new Goal(
                    new Dictionary<ThingDef, int>(ShipUtility.RequiredParts()),
                    goal => { goal.parts[ThingDefOf.Ship_CryptosleepCasket] = Find.CurrentMap.mapPawns.FreeColonistsCount; });

                public static readonly Goal shipAllColonists = new Goal(
                    new Dictionary<ThingDef, int>(ShipUtility.RequiredParts()),
                    goal => { goal.parts[ThingDefOf.Ship_CryptosleepCasket] = PawnsFinder.AllMapsCaravansAndTravelingTransportPods_Alive_FreeColonists.Count(); });
            }
        }

        [HarmonyPatch(typeof(ResourceCounter), nameof(ResourceCounter.ResourceCounterTick))]
        static class ResourceCounter_ResourceCounterTick_Patch
        {
            [HarmonyPostfix]
            static void CounterTick() {
                curGoal.CounterTick();
            }
        }

        [HarmonyPatch(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI))]
        static class ResourceReadout_ResourceReadoutOnGUI_Patch
        {
            static readonly MethodInfo DrawIconMethod = AccessTools.Method(typeof(ResourceReadout), "DrawIcon");
            static float lastDrawnHeight;
            static Vector2 scrollPosition;

            [HarmonyPostfix]
            static void ResourceGoalReadout(ResourceReadout __instance) {
                if (Event.current.type == EventType.layout) return;
                if (Current.ProgramState != ProgramState.Playing) return;
                if (Find.MainTabsRoot.OpenTab == MainButtonDefOf.Menu) return;

                GenUI.DrawTextWinterShadow(new Rect(256f, 512f, -256f, -512f)); // copied from ResourceReadout, not sure exactly
                Text.Font = GameFont.Small;

                var readoutRect = new Rect(120f + 7f, 7f, 110f, UI.screenHeight - 7 - 200f);
                var viewRect = new Rect(0f, 0f, readoutRect.width, lastDrawnHeight);
                var needScroll = viewRect.height > readoutRect.height;
                if (needScroll) {
                    Widgets.BeginScrollView(readoutRect, ref scrollPosition, viewRect, false);
                } else {
                    scrollPosition = Vector2.zero;
                    GUI.BeginGroup(readoutRect);
                }

                GUI.BeginGroup(viewRect);
                Text.Anchor = TextAnchor.MiddleLeft;
                DrawResource(__instance, readoutRect, out lastDrawnHeight);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.EndGroup();

                if (needScroll)
                    Widgets.EndScrollView();
                else
                    GUI.EndGroup();
            }

            static void DrawResource(ResourceReadout __instance, Rect readoutRect, out float drawHeight) {
                drawHeight = 0f;
                foreach (var amount in curGoal.resourceAmounts) {
                    if (amount.Value <= 0) continue;
                    var iconRect = new Rect(0f, drawHeight, 999f, 24f);
                    if (iconRect.yMax >= scrollPosition.y && iconRect.y <= scrollPosition.y + readoutRect.height) {
                        DrawIconMethod.Invoke(__instance, new object[] {iconRect.x, iconRect.y, amount.Key});
                        iconRect.y += 2f;
                        var labelRect = new Rect(34f, iconRect.y, iconRect.width - 34f, iconRect.height);
                        Widgets.Label(labelRect, amount.Value.ToStringCached());
                    }

                    drawHeight += 24f;

                    if (Event.current.type == EventType.MouseUp && Event.current.button == 1 && Mouse.IsOver(new Rect(iconRect.x, iconRect.y, 100f, 24f))) {
                        Event.current.Use();
                        Find.WindowStack.Add(Goal.FloatMenu(amount.Key));
                    }
                }
            }
        }
    }
}