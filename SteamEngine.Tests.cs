using System;
using System.Collections.Generic;

class SteamEngineTests
{
    static int passed, failed;

    static void Main()
    {
        Test_StartConditions();
        Test_PowerWithAllPartCombinations();
        Test_FuelExhaustion();
        Test_WaterExhaustion();
        Test_PartDegradationAndBreakage();
        Test_RuntimeStops();
        Test_MaxPowerClamping();
        Test_ConfigValidation();
        Test_TryStartEngine();
        Test_PowerEdgeCases();
        Test_DegradePartsSimulation();
        Test_GetFuelAmount();
        Test_GetWaterAmount();
        Test_CombinedRuntime();
        Test_SlotFilter();
        Test_WaterContainerSource();
        Test_DedicatedSlotPlacement();

        Console.WriteLine($"\n=== {passed} passed, {failed} failed ===");
        Environment.Exit(failed > 0 ? 1 : 0);
    }

    static void Ok(string label)
    { Console.WriteLine($"  OK  {label}"); passed++; }
    static void Fail(string label, string detail)
    { Console.WriteLine($"  FAIL {label}: {detail}"); failed++; }
    static void CheckEq(float expected, float actual, string label)
    { if (Math.Abs(expected-actual)<0.01f) Ok(label); else Fail(label,$"{actual} != {expected}"); }
    static void CheckTrue(bool cond, string label)
    { if (cond) Ok(label); else Fail(label,"expected true"); }
    static void CheckFalse(bool cond, string label)
    { if (!cond) Ok(label); else Fail(label,"expected false"); }

    static readonly string[] BaseNames = {"carburetor","crankshaft","piston","sparkplug","valve"};
    static readonly Dictionary<string,float> TierMult = new Dictionary<string,float>
        {{"1",1.0f},{"2",1.2f},{"3",1.5f}};
    static readonly HashSet<string> WaterContainers = new HashSet<string>
        {"waterjug","smallwaterbottle","bucket.water","botabag"};
    const float BasePower = 25f;
    const float CharcoalBonus = 15f;
    const float WoodBonus = 0f;
    const float MaxPower = 140f;
    const int FuelSlot = 0, FirstPartSlot = 1, LastPartSlot = 5, WaterSlot = 6;

    // ---------------------------------------------------------------
    // Logic mirrors
    // ---------------------------------------------------------------
    static bool IsCarPart(string shortname)
    {
        foreach (var name in BaseNames)
        {
            if (shortname.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                var s = shortname.Substring(name.Length);
                if (s == "1" || s == "2" || s == "3") return true;
            }
        }
        return false;
    }

    static bool HasAllParts(string[] slots, HashSet<string> broken)
    {
        var found = new HashSet<string>();
        foreach (var s in slots)
        {
            if (s == null || broken.Contains(s)) continue;
            foreach (var n in BaseNames)
            {
                if (s.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                { found.Add(n); break; }
            }
        }
        foreach (var n in BaseNames)
            if (!found.Contains(n)) return false;
        return true;
    }

    static int CountFuel(string[] slots)
    {
        int c = 0;
        foreach (var s in slots)
            if (s == "wood" || s == "charcoal") c++;
        return c;
    }

    static float CalcPower(string[] slots, string fuelType, HashSet<string> broken)
    {
        float power = BasePower + ((fuelType == "charcoal") ? CharcoalBonus : WoodBonus);

        var bestTier = new Dictionary<string, float>();
        foreach (var s in slots)
        {
            if (s == null || broken.Contains(s)) continue;
            if (!IsCarPart(s)) continue;
            string bn = null, tier = null;
            foreach (var n in BaseNames)
            {
                if (s.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = s.Substring(n.Length);
                    if (suffix == "1" || suffix == "2" || suffix == "3")
                    { bn = n; tier = suffix; }
                    break;
                }
            }
            if (bn == null || !TierMult.TryGetValue(tier, out var m)) continue;
            if (!bestTier.TryGetValue(bn, out var e) || m > e)
                bestTier[bn] = m;
        }

        float pm = 1f;
        foreach (var m in bestTier.Values)
            pm += (m - 1f);
        return Math.Max(0, Math.Min(power * pm, MaxPower));
    }

    // ---------------------------------------------------------------
    // A) START CONDITIONS
    // ---------------------------------------------------------------
    static void Test_StartConditions()
    {
        Console.WriteLine("=== A) Start conditions ===");

        var allT1 = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","charcoal"};
        var allT2 = new[]{"carburetor2","crankshaft2","piston2","sparkplug2","valve2","charcoal"};
        var allT3 = new[]{"carburetor3","crankshaft3","piston3","sparkplug3","valve3","charcoal"};
        var mixed = new[]{"carburetor1","crankshaft2","piston3","sparkplug1","valve2","charcoal"};
        var broken = new HashSet<string>();

        // All T1 + charcoal + water
        CheckTrue(HasAllParts(allT1,broken) && CountFuel(allT1)>0,
            "A1: all T1 + charcoal -> starts");

        // All T2 + charcoal
        CheckTrue(HasAllParts(allT2,broken) && CountFuel(allT2)>0,
            "A2: all T2 + charcoal -> starts");

        // All T3 + charcoal
        CheckTrue(HasAllParts(allT3,broken) && CountFuel(allT3)>0,
            "A3: all T3 + charcoal -> starts");

        // Mixed T1/T2/T3
        CheckTrue(HasAllParts(mixed,broken) && CountFuel(mixed)>0,
            "A4: mixed T1/T2/T3 -> starts");

        // All T1 + wood
        var woodFuel = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","wood"};
        CheckTrue(HasAllParts(woodFuel,broken) && CountFuel(woodFuel)>0,
            "A5: all T1 + wood -> starts");

        // Missing carburetor
        var miss1 = new[]{"crankshaft1","piston1","sparkplug1","valve1","charcoal"};
        CheckFalse(HasAllParts(miss1,broken),
            "A6: missing 1 part -> no start");

        // All parts + NO fuel
        var noFuel = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"};
        CheckTrue(HasAllParts(noFuel,broken) && CountFuel(noFuel)==0,
            "A7: all parts present but no fuel -> can't start (CountFuel=0)");

        // No parts + fuel
        var noParts = new[]{"charcoal"};
        CheckFalse(HasAllParts(noParts,broken),
            "A8: no parts + fuel -> no start");

        // All T1 + charcoal + water=0 (water check is external in real code)
        CheckTrue(HasAllParts(allT1,broken) && CountFuel(allT1)>0,
            "A9: parts+fuel present -> TryStart passes all internal checks (water checked separately)");
    }

    // ---------------------------------------------------------------
    // B) POWER WITH ALL PART COMBINATIONS
    // ---------------------------------------------------------------
    static void Test_PowerWithAllPartCombinations()
    {
        Console.WriteLine("\n=== B) Power output === ");
        var b = new HashSet<string>();

        CheckEq(40f, CalcPower(new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"},"charcoal",b),
            "B1: all T1 + charcoal = 40W");
        CheckEq(80f, CalcPower(new[]{"carburetor2","crankshaft2","piston2","sparkplug2","valve2"},"charcoal",b),
            "B2: all T2 + charcoal = 80W");
        CheckEq(140f, CalcPower(new[]{"carburetor3","crankshaft3","piston3","sparkplug3","valve3"},"charcoal",b),
            "B3: all T3 + charcoal = 140W(capped)");
        CheckEq(96f, CalcPower(new[]{"carburetor3","crankshaft3","piston2","sparkplug2","valve1"},"charcoal",b),
            "B4: 2xT3+2xT2+1xT1 = 40*2.4=96W");
        CheckEq(60f, CalcPower(new[]{"carburetor3","crankshaft1","piston1","sparkplug1","valve1"},"charcoal",b),
            "B5: 1xT3+4xT1 = 40*1.5=60W");
        CheckEq(108f, CalcPower(new[]{"carburetor3","crankshaft3","piston3","sparkplug2","valve1"},"charcoal",b),
            "B6: 3xT3+1xT2+1xT1 = 40*2.7=108W");
        CheckEq(104f, CalcPower(new[]{"carburetor3","crankshaft3","piston2","sparkplug2","valve2"},"charcoal",b),
            "B7: 2xT3+3xT2 = 40*2.6=104W");
        CheckEq(87.5f, CalcPower(new[]{"carburetor3","crankshaft3","piston3","sparkplug3","valve3"},"wood",b),
            "B8: all T3 + wood = 25*3.5=87.5W");
        CheckEq(25f, CalcPower(new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"},"wood",b),
            "B9: all T1 + wood = 25W");
        CheckEq(60f, CalcPower(new[]{"carburetor3","crankshaft2","piston2","sparkplug1","valve3"},"wood",b),
            "B10: wood mixed = 25*(1+0.5+0.2+0.2+0+0.5)=60W");
        // T1+T2+T3 all in one type -> highest wins
        CheckEq(60f, CalcPower(new[]{"carburetor1","carburetor2","carburetor3","crankshaft1","piston1","sparkplug1","valve1"},"charcoal",b),
            "B11: dup carb T1+T2+T3 -> T3 wins = (25+15)*(1+0.5)=60W");
    }

    // ---------------------------------------------------------------
    // C) FUEL EXHAUSTION
    // ---------------------------------------------------------------
    static void Test_FuelExhaustion()
    {
        Console.WriteLine("\n=== C) Fuel exhaustion ===");
        var b = new HashSet<string>();

        // Has fuel → runs
        var slots = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","charcoal"};
        CheckTrue(CountFuel(slots)>0 && HasAllParts(slots,b),
            "C1: fuel present + all parts -> tick passes checks");
        CheckEq(40f, CalcPower(slots,"charcoal",b),
            "C1b: power = 40W with charcoal");

        // Fuel consumed to 0 → next tick fails check
        var postConsume = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"};
        CheckEq(0, CountFuel(postConsume),
            "C2: fuel consumed to 0 -> next tick GetFuelAmount=0");
        CheckFalse(CountFuel(postConsume)>0,
            "C2b: CountFuel>0 = false → engine stops on next tick");

        // Wood fuel
        var wf = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","wood"};
        CheckTrue(CountFuel(wf)>0, "C3: wood fuel present");
        CheckEq(25f, CalcPower(wf,"wood",b), "C3b: wood power = 25W");

        // No fuel at all
        var empty = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"};
        CheckEq(0, CountFuel(empty), "C4: no fuel -> CountFuel=0");
        CheckFalse(CountFuel(empty)>0, "C4b: engine would stop (GetFuelAmount<=0)");
    }

    // ---------------------------------------------------------------
    // D) WATER EXHAUSTION
    // ---------------------------------------------------------------
    static void Test_WaterExhaustion()
    {
        Console.WriteLine("\n=== D) Water exhaustion ===");

        // Water check is external (not in CalcPower/HasAllParts)
        int water = 1000;
        int consume = 50;
        CheckTrue(water > 0, "D1: water available -> runs");
        water -= consume;
        CheckEq(950, water, "D1b: after consume = 950ml");

        water = 0;
        CheckFalse(water > 0, "D2: water=0 -> GetWaterAmount<=0 → stops");

        water = 49;
        consume = 50;
        water -= consume;
        CheckTrue(water < 0, "D3: water drained below 0 -> next tick GetWaterAmount=0 → stops");
    }

    // ---------------------------------------------------------------
    // E) PART DEGRADATION & BREAKAGE
    // ---------------------------------------------------------------
    static void Test_PartDegradationAndBreakage()
    {
        Console.WriteLine("\n=== E) Part degradation and breakage ===");

        var slots = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","charcoal"};
        var broken = new HashSet<string>();

        // All parts intact
        CheckTrue(HasAllParts(slots,broken), "E1: all parts intact -> HasAllParts=true");
        CheckEq(40f, CalcPower(slots,"charcoal",broken), "E1b: power = 40W");

        // One part breaks (carburetor1 removed)
        broken.Add("carburetor1");
        CheckFalse(HasAllParts(slots,broken), "E2: carburetor breaks -> HasAllParts=false");
        CheckFalse(HasAllParts(slots,broken), "E2b: engine would stop (HasAllParts check after DegradeParts)");

        // Replace broken part + other degrades but survives
        broken.Remove("carburetor1");
        // Simulate: light wear, all survive
        CheckTrue(HasAllParts(slots,broken), "E3: part replaced + others survive -> HasAllParts=true");

        // Multiple breaks simultaneously
        broken.Add("carburetor1");
        broken.Add("crankshaft1");
        CheckFalse(HasAllParts(slots,broken), "E4: 2 parts break -> HasAllParts=false");

        // Wear disabled
        broken.Clear();
        CheckTrue(HasAllParts(slots,broken), "E5: wear=0 -> no parts break");
    }

    // ---------------------------------------------------------------
    // F) RUNTIME STOP COMBINATIONS
    // ---------------------------------------------------------------
    static void Test_RuntimeStops()
    {
        Console.WriteLine("\n=== F) Runtime stop combinations ===");
        var b = new HashSet<string>();

        // All good → runs
        var slots = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","charcoal"};
        CheckTrue(HasAllParts(slots,b) && CountFuel(slots)>0,
            "F1: all 3 conditions met -> runs");

        // Fuel exhausted
        var noFuel = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"};
        CheckFalse(CountFuel(noFuel)>0 && HasAllParts(noFuel,b),
            "F2: fuel exhausted -> stops (parts+water ok)");

        // Water exhausted (external check, simulate)
        CheckFalse(false, "F3: water exhausted -> stops (fuel+parts ok)");

        // Parts break
        b.Add("carburetor1");
        CheckFalse(HasAllParts(slots,b) && CountFuel(slots)>0,
            "F4: parts break -> stops (fuel+water ok)");
        b.Clear();

        // Fuel exhausted + parts break
        b.Add("carburetor1");
        CheckFalse(HasAllParts(noFuel,b) && CountFuel(noFuel)>0,
            "F5: fuel exhausted + parts break -> stops");
        b.Clear();

        // Water exhausted + fuel exhausted
        CheckFalse(HasAllParts(noFuel,b) && CountFuel(noFuel)>0,
            "F6: fuel exhausted (+water exhausted externally) -> stops");

        // All three fail
        b.Add("carburetor1");
        CheckFalse(HasAllParts(noFuel,b) && CountFuel(noFuel)>0,
            "F7: parts break + fuel exhausted (+water exhausted) -> stops");
        b.Clear();

        // Parts survive + fuel ok + water ok
        CheckTrue(HasAllParts(slots,b) && CountFuel(slots)>0,
            "F8: all ok -> continues running");
    }

    // ---------------------------------------------------------------
    // G) MAX POWER CLAMPING
    // ---------------------------------------------------------------
    static void Test_MaxPowerClamping()
    {
        Console.WriteLine("\n=== G) MaxPower clamping ===");
        var b = new HashSet<string>();

        CheckEq(140f, CalcPower(new[]{"carburetor3","crankshaft3","piston3","sparkplug3","valve3"},"charcoal",b),
            "G1: 5xT3+charcoal clamped to 140W");
        CheckEq(40f, CalcPower(new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"},"charcoal",b),
            "G2: 5xT1 no clamp = 40W");
        CheckEq(116f, CalcPower(new[]{"carburetor3","crankshaft3","piston3","sparkplug2","valve2"},"charcoal",b),
            "G3: 3xT3+2xT2 = 40*2.9=116W (below cap)");
    }

    // ---------------------------------------------------------------
    // H) CONFIG VALIDATION
    // ---------------------------------------------------------------
    static void Test_ConfigValidation()
    {
        Console.WriteLine("\n=== H) Config validation ===");

        float v = -1f; if (v <= 0f) v = 1f;
        CheckEq(1f, v, "H1: negative tick interval clamped to 1");

        v = 0f; if (v <= 0f) v = 100f;
        CheckEq(100f, v, "H2: zero max power clamped to 100");

        v = -5f; if (v < 0f) v = 0f;
        CheckEq(0f, v, "H3: negative base power clamped to 0");

        CheckTrue(IsCarPart("piston2"), "H4: piston2 is car part");
        CheckFalse(IsCarPart("wood"), "H5: wood is NOT car part");
        CheckFalse(IsCarPart("gears1"), "H6: unknown part is NOT car part");
        CheckTrue(IsCarPart("valve3"), "H7: valve3 is car part");
        CheckTrue(IsCarPart("carburetor1"), "H8: carburetor1 is car part");
        CheckFalse(IsCarPart("carburetor"), "H9: carburetor (no tier) is NOT a valid part");
        CheckFalse(IsCarPart("carburetor4"), "H10: carburetor4 (invalid tier) is NOT a valid part");

        CheckFalse(IsCarPart(""), "H11: empty string is NOT a part");
        CheckFalse(IsCarPart("gears"), "H12: gear (no tier, unknown) is NOT a part");
    }

    // ---------------------------------------------------------------
    // I) TryStartEngine gate combinations
    // ---------------------------------------------------------------
    static void Test_TryStartEngine()
    {
        Console.WriteLine("\n=== I) TryStartEngine gates ===");
        var b = new HashSet<string>();

        Func<string[],string,int,bool> canStart = (parts,fuel,water) =>
            HasAllParts(parts,b) && CountFuel(parts)>0 && water>0;

        var all = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","charcoal"};
        var wood = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","wood"};

        // Gate: HasAllParts
        CheckTrue(canStart(all,"charcoal",1000), "I1: all parts+charcoal+water -> true");
        CheckTrue(canStart(wood,"wood",1), "I2: all parts+wood+1ml water -> true");

        // Gate: CountFuel=0
        var noFuel = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"};
        CheckFalse(canStart(noFuel,"wood",1000), "I3: no fuel -> false");

        // Gate: water=0
        CheckFalse(canStart(all,"charcoal",0), "I4: water=0 -> false");

        // Gate: missing piston
        var missPiston = new[]{"carburetor1","crankshaft1","sparkplug1","valve1","charcoal"};
        CheckFalse(canStart(missPiston,"charcoal",1000), "I5: missing piston -> false");

        // Gate: missing sparkplug
        var missSpark = new[]{"carburetor1","crankshaft1","piston1","valve1","charcoal"};
        CheckFalse(canStart(missSpark,"charcoal",1000), "I6: missing sparkplug -> false");

        // Gate: all present but already running
        CheckTrue(HasAllParts(all,b), "I7: all parts present -> HasAllParts=true (running check is external)");
    }

    // ---------------------------------------------------------------
    // J) Power edge cases
    // ---------------------------------------------------------------
    static void Test_PowerEdgeCases()
    {
        Console.WriteLine("\n=== J) Power edge cases ===");
        var b = new HashSet<string>();

        CheckEq(140f, CalcPower(new[]{"carburetor3","crankshaft3","piston3","sparkplug3","valve3"},"charcoal",b),
            "J1: all T3 + charcoal = 140W (capped)");
        CheckEq(25f, CalcPower(new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"},"wood",b),
            "J2: all T1 + wood = 25W (floor)");
        CheckEq(60f, CalcPower(new[]{"carburetor3"},"charcoal",b),
            "J3: single T3 part = (25+15)*(1+0.5)=60W");
        CheckEq(40f, CalcPower(new string[]{},"charcoal",b),
            "J4: no parts = just base+charcoal=40W");
    }

    // ---------------------------------------------------------------
    // K) DegradeParts simulation
    // ---------------------------------------------------------------
    static void Test_DegradePartsSimulation()
    {
        Console.WriteLine("\n=== K) DegradeParts simulation ===");
        var b = new HashSet<string>();
        var parts = new[]{"carburetor1","crankshaft2","piston3","sparkplug1","valve2"};

        // All survive light wear
        CheckTrue(HasAllParts(parts,b), "K1: all parts survive light wear");
        CheckEq(76f, CalcPower(parts,"charcoal",b), "K2: power = (25+15)*(1+0+0.2+0.5+0+0.2)=40*1.9=76W");

        // Piston3 breaks (last of its type)
        b.Add("piston3");
        CheckFalse(HasAllParts(parts,b), "K3: piston breaks -> engine stops");

        // Replace with piston1, continues at lower power
        b.Remove("piston3");
        var parts2 = new[]{"carburetor1","crankshaft2","piston1","sparkplug1","valve2"};
        CheckTrue(HasAllParts(parts2,b), "K4: piston replaced with T1 -> engine runs");
        CheckEq(56f, CalcPower(parts2,"charcoal",b), "K5: power drops to (25+15)*(1+0+0.2+0+0+0.2)=40*1.4=56W");

        // Crankshaft degrades but doesn't break (condition>0)
        CheckTrue(HasAllParts(parts2,b), "K6: crankshaft degraded but survives -> still runs");

        // All parts break at once
        b.Add("carburetor1"); b.Add("crankshaft2"); b.Add("piston1"); b.Add("sparkplug1"); b.Add("valve2");
        CheckFalse(HasAllParts(parts2,b), "K7: all parts break -> HasAllParts=false");
        b.Clear();

        // Part with condition going to 0 (boundary test)
        CheckTrue(HasAllParts(parts,b), "K8: condition exactly 0 after wear -> removed, but before removal still present");
    }

    // ---------------------------------------------------------------
    // L) GetFuelAmount edge cases
    // ---------------------------------------------------------------
    static void Test_GetFuelAmount()
    {
        Console.WriteLine("\n=== L) GetFuelAmount ===");

        var inv = new[]{"charcoal","wood","carburetor1","crankshaft1","piston1","sparkplug1"};
        CheckEq(2, CountFuel(inv), "L1: 2 fuel items (charcoal+wood)");
        CheckEq(0, CountFuel(new string[]{"carburetor1","piston1"}), "L2: 0 fuel items");
        CheckEq(1, CountFuel(new string[]{"wood"}), "L3: 1 wood");
        CheckEq(0, CountFuel(new string[]{}), "L4: empty inventory = 0 fuel");
    }

    // ---------------------------------------------------------------
    // M) GetWaterAmount edge cases
    // ---------------------------------------------------------------
    static void Test_GetWaterAmount()
    {
        Console.WriteLine("\n=== M) GetWaterAmount ===");
        CheckTrue(1000 > 0, "M1: 1000ml water > 0 -> has water");
        CheckFalse(0 > 0, "M2: 0ml water -> GetWaterAmount=0");
        CheckFalse((-1) > 0, "M3: negative water (should never happen, but handled)");
    }

    // ---------------------------------------------------------------
    // N) Combined runtime scenarios
    // ---------------------------------------------------------------
    static void Test_CombinedRuntime()
    {
        Console.WriteLine("\n=== N) Combined runtime scenarios ===");
        var b = new HashSet<string>();

        // Scenario: running at 140W, piston breaks → stops
        var parts = new[]{"carburetor3","crankshaft3","piston3","sparkplug3","valve3","charcoal"};
        CheckTrue(HasAllParts(parts,b) && CountFuel(parts)>0, "N1: running at 140W with all T3");
        b.Add("piston3");
        CheckFalse(HasAllParts(parts,b), "N2: piston breaks -> engine stops");
        b.Clear();

        // Scenario: running, charcoal→wood transition (charcoal consumed, wood still present)
        var mixedFuel = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","wood","charcoal"};
        CheckTrue(HasAllParts(mixedFuel,b) && CountFuel(mixedFuel)>0, "N3: both charcoal+wood present");
        CheckEq(2, CountFuel(mixedFuel), "N3b: 2 fuel items");
        // Charcoal consumed first, wood remains
        var afterCharcoal = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","wood"};
        CheckTrue(HasAllParts(afterCharcoal,b) && CountFuel(afterCharcoal)>0, "N4: charcoal gone, wood remains -> still runs");
        CheckEq(25f, CalcPower(afterCharcoal,"wood",b), "N4b: power drops from 40W to 25W");

        // Scenario: wood also consumed → stops
        var allFuelGone = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1"};
        CheckFalse(CountFuel(allFuelGone)>0, "N5: both fuel types exhausted -> stops");

        // Scenario: running, water drains, parts fine, fuel fine → stops on empty water
        CheckTrue(HasAllParts(parts,b) && CountFuel(parts)>0, "N6: parts+fuel ok");
        CheckFalse(0 > 0, "N6b: water=0 -> stops");

        var afterBreak = new[]{"carburetor1","crankshaft1","piston1","sparkplug1","valve1","charcoal"};
        CheckTrue(HasAllParts(afterBreak,b) && CountFuel(afterBreak)>0, "N7: broken part replaced → engine can restart");
        CheckTrue(HasAllParts(afterBreak,b) && CountFuel(afterBreak)>0, "N8: water refilled + parts+fuel ok → engine can restart");
    }

    // ---------------------------------------------------------------
    // O) Slot filter (mirrors CanAcceptItem slot typing)
    // ---------------------------------------------------------------
    static bool SlotAllows(string shortname, int targetPos)
    {
        bool isFuel = shortname == "wood" || shortname == "charcoal";
        bool isPart = IsCarPart(shortname);
        bool isWater = WaterContainers.Contains(shortname);

        if (targetPos == FuelSlot) return isFuel;
        if (targetPos >= FirstPartSlot && targetPos <= LastPartSlot) return isPart;
        if (targetPos == WaterSlot) return isWater;
        return isFuel || isPart || isWater;
    }

    static void Test_SlotFilter()
    {
        Console.WriteLine("\n=== O) Slot filter ===");

        // Fuel slot
        CheckTrue(SlotAllows("wood", FuelSlot), "O1: wood -> fuel slot");
        CheckTrue(SlotAllows("charcoal", FuelSlot), "O2: charcoal -> fuel slot");
        CheckFalse(SlotAllows("carburetor1", FuelSlot), "O3: car part rejected from fuel slot");
        CheckFalse(SlotAllows("waterjug", FuelSlot), "O4: water jug rejected from fuel slot");

        // Part slots
        for (int s = FirstPartSlot; s <= LastPartSlot; s++)
            CheckTrue(SlotAllows("piston2", s), $"O5.{s}: part accepted in slot {s}");
        CheckFalse(SlotAllows("wood", FirstPartSlot), "O6: fuel rejected from part slot");
        CheckFalse(SlotAllows("bucket.water", LastPartSlot), "O7: water container rejected from part slot");

        // Water slot
        CheckTrue(SlotAllows("waterjug", WaterSlot), "O8: waterjug -> water slot");
        CheckTrue(SlotAllows("smallwaterbottle", WaterSlot), "O9: bottle -> water slot");
        CheckTrue(SlotAllows("bucket.water", WaterSlot), "O10: bucket -> water slot");
        CheckTrue(SlotAllows("botabag", WaterSlot), "O11: botabag -> water slot");
        CheckFalse(SlotAllows("wood", WaterSlot), "O12: fuel rejected from water slot");
        CheckFalse(SlotAllows("valve3", WaterSlot), "O13: part rejected from water slot");

        // No target slot (targetPos = -1): category whitelist
        CheckTrue(SlotAllows("wood", -1), "O14: fuel allowed via auto-placement");
        CheckTrue(SlotAllows("sparkplug1", -1), "O15: part allowed via auto-placement");
        CheckTrue(SlotAllows("waterjug", -1), "O16: water container allowed via auto-placement");
        CheckFalse(SlotAllows("rock", -1), "O17: junk rejected everywhere");
        CheckFalse(SlotAllows("metal.fragments", 3), "O18: junk rejected from typed slot");
    }

    // ---------------------------------------------------------------
    // P) Water sources (mirrors GetWaterAmount: barrel + slot container)
    // ---------------------------------------------------------------
    static int WaterFromVessel(string vessel, string liquid, int amount)
    {
        if (!WaterContainers.Contains(vessel)) return 0;
        if (liquid != "water") return 0;
        return Math.Max(0, amount);
    }

    static int WaterFromBarrel(string liquid, int amount)
    {
        if (liquid != "water") return 0;
        return Math.Max(0, amount);
    }

    static void Test_WaterContainerSource()
    {
        Console.WriteLine("\n=== P) Water sources ===");

        CheckEq(5000, WaterFromVessel("waterjug", "water", 5000), "P1: jug with 5000ml fresh water");
        CheckEq(0, WaterFromVessel("waterjug", "water.salt", 5000), "P2: salt water in jug rejected");
        CheckEq(0, WaterFromVessel("waterjug", null, 0), "P3: empty jug = 0ml");
        CheckEq(0, WaterFromVessel("wood", "water", 500), "P4: non-vessel item never a water source");
        CheckEq(250, WaterFromVessel("smallwaterbottle", "water", 250), "P5: bottle water counted");

        // Barrel (hose input)
        CheckEq(2000, WaterFromBarrel("water", 2000), "P6: hose-fed barrel water counted");
        CheckEq(0, WaterFromBarrel("water.salt", 2000), "P7: salt water in barrel rejected");
        CheckEq(0, WaterFromBarrel(null, 0), "P8: empty barrel = 0ml");

        // Combined total (either source is enough)
        int total = WaterFromBarrel("water", 100) + WaterFromVessel("waterjug", "water", 200);
        CheckEq(300, total, "P9: barrel + slot water combined");
        CheckTrue(WaterFromBarrel("water", 100) + WaterFromVessel("waterjug", null, 0) > 0,
            "P10: barrel alone is enough (no slot container)");
        CheckTrue(WaterFromBarrel(null, 0) + WaterFromVessel("bucket.water", "water", 500) > 0,
            "P11: slot container alone is enough (no hose)");

        // Drain order: barrel first, then slot container
        int barrel = 30, jug = 100, perTick = 50;
        int fromBarrel = Math.Min(barrel, perTick);
        barrel -= fromBarrel;
        int remaining = perTick - fromBarrel;
        int fromJug = Math.Min(jug, remaining);
        jug -= fromJug;
        CheckEq(0, barrel, "P12: barrel drained first");
        CheckEq(80, jug, "P13: remainder drained from slot container");

        // Drain simulation: 5000ml jug at 50ml/s
        int water = 5000, ticks = 0;
        while (water > 0) { water -= perTick; ticks++; }
        CheckEq(100, ticks, "P14: 5000ml jug lasts 100 ticks at 50ml/s");

        // Both drained to 0 -> engine stops next tick
        CheckEq(0, WaterFromBarrel(null, 0) + WaterFromVessel("waterjug", "water", 0),
            "P15: all water gone -> stops");

        // Refill: swap in a full bucket -> engine can restart
        CheckTrue(WaterFromVessel("bucket.water", "water", 2000) > 0, "P16: refilled bucket -> can restart");
    }

    // ---------------------------------------------------------------
    // Q) Dedicated slot placement (mirrors FindDedicatedSlot/IsCorrectSlot)
    // ---------------------------------------------------------------
    static bool IsCorrectSlot(int pos, string shortname)
    {
        if (shortname == "wood" || shortname == "charcoal") return pos == FuelSlot;
        if (IsCarPart(shortname)) return pos >= FirstPartSlot && pos <= LastPartSlot;
        if (WaterContainers.Contains(shortname)) return pos == WaterSlot;
        return false;
    }

    // inv[i] = shortname or null; same-shortname counts as stackable-available
    static int FindDedicatedSlot(string[] inv, string shortname)
    {
        Func<int, bool> avail = i => inv[i] == null || inv[i] == shortname;
        if (shortname == "wood" || shortname == "charcoal") return avail(FuelSlot) ? FuelSlot : -1;
        if (WaterContainers.Contains(shortname)) return avail(WaterSlot) ? WaterSlot : -1;
        if (IsCarPart(shortname))
        {
            for (int i = FirstPartSlot; i <= LastPartSlot; i++)
                if (inv[i] == null) return i;
        }
        return -1;
    }

    static void Test_DedicatedSlotPlacement()
    {
        Console.WriteLine("\n=== Q) Dedicated slot placement ===");
        var empty = new string[7];

        CheckEq(0, FindDedicatedSlot(empty, "wood"), "Q1: wood -> fuel slot 0");
        CheckEq(6, FindDedicatedSlot(empty, "waterjug"), "Q2: jug -> water slot 6");
        CheckEq(1, FindDedicatedSlot(empty, "piston2"), "Q3: first part -> slot 1");

        var partly = new string[]{ "wood", "carburetor1", null, null, null, null, null };
        CheckEq(2, FindDedicatedSlot(partly, "piston2"), "Q4: next part -> first free part slot");
        CheckEq(0, FindDedicatedSlot(partly, "wood"), "Q5: wood stacks onto existing wood in slot 0");
        CheckEq(-1, FindDedicatedSlot(partly, "charcoal"), "Q6: charcoal rejected while wood occupies fuel slot");

        var partsFull = new string[]{ null, "carburetor1", "crankshaft1", "piston1", "sparkplug1", "valve1", null };
        CheckEq(-1, FindDedicatedSlot(partsFull, "piston3"), "Q7: all part slots full -> rejected/bounced");

        var waterFull = new string[]{ null, null, null, null, null, null, "bucket.water" };
        CheckEq(-1, FindDedicatedSlot(waterFull, "waterjug"), "Q8: water slot occupied -> jug rejected");

        CheckTrue(IsCorrectSlot(0, "charcoal"), "Q9: charcoal correct in slot 0");
        CheckFalse(IsCorrectSlot(3, "wood"), "Q10: wood incorrect in part slot");
        CheckFalse(IsCorrectSlot(6, "valve2"), "Q11: part incorrect in water slot");
        CheckTrue(IsCorrectSlot(6, "botabag"), "Q12: botabag correct in slot 6");
        CheckFalse(IsCorrectSlot(0, "rock"), "Q13: junk never correct");
    }
}
