# Inventory demo (D): a bag over time, through the Inventory pack

A backpack plays a 10 s **Loot Run** timeline: items are given, one is taken, four Wood are crafted into two Planks, and two
**Pickup markers** drop 10 Coins and a Gem. Top left: the tick and the bag. At the end (step 600) the bag holds
`Wood 1 Stone 3 Plank 2 Potion 1 Sword 1 Coin 10 Key 0 Gem 1`, the same as the .NET pack test.

Needs the Inventory pack next to `com.tl.unity`: Package Manager > **+** > **Add package from git URL** >
`https://github.com/IAFahim/tl.git?path=/packs/Inventory` (the Tl installers add it for you).

## First 10 minutes

1. Package Manager > **Tl Unity Playback** > **Samples** > **Inventory demo** > **Import**. **Inventory Demo** opens
   (`Tl > Samples > Build Inventory Demo` rebuilds it). Press **Play** and watch the bag fill: Wood 5 at once, Stone at 1 s, Potions at
   2 s, the craft takes 4 Wood at 3 s and gives 2 Planks at 5 s, a Potion goes at 6 s, Coins at 7 s, the Gem at 8 s, the Sword at 9 s.
2. Tick **Inventory Demo Objects**, select **Backpack**, `Window > Sequencing > Timeline`: **Loot Run** has a **Give**, a **Take** and a
   **Craft** track (Timeline **+** > **Inventory**). The two diamonds at the top are the Pickup markers: right-click the marker row >
   **PickupMarker** adds another one; select it to set Item and Count.
3. Add a Pickup at 8.33 s with Item **Key**, Count 3, press Play: the bag ends with Key 3.
4. The **Tl Bag** component on the Backpack holds the starting items (all 0 here). Set Stone to 2: the bag ends with Stone 5. Tl keeps
   these values; it only adds the bag when nothing else did.
5. Read the pack (Packages > **Tl Inventory Pack** > `Inventory.cs`): `GiveClip`, `CraftClip` and `PickupMarker` are plain structs, and three
   pulse methods turn them into `ItemDelta`s for the `InventoryItems` kind. No game code in this demo.

## What the designer builds

| Asset / object | Content |
|---|---|
| `Loot Run.playable` | Give: Wood 5 at tick 0, Stone 3 at 60, Potion 2 at 120, Sword 1 at 540 (1-tick clips). Take (a Give track): Potion -1 at 360. Craft: ticks 180-300, 4 Wood into 2 Plank. Markers: Pickup Coin 10 at tick 420, Pickup Gem 1 at 480. 10 s, **Hold**. |
| Backpack (SubScene) | Playable Director on Loot Run (Play On Awake), **Tl Bag** (all 0). |

## How it works

Every pulse is signed by the play direction (`frame.Direction`, `marker.Direction`), so a step backwards undoes exactly what the step
forwards did. A clip's start pulse (Give, Craft's consume) lands on its first tick, Craft's produce on its last tick (299), a marker on
its own tick.

## Checked headless

`Tests/InventoryDemoTests.cs` (PlayMode, default world, the built scene) follows the backpack to step 600 and fails on any wrong count:
the bag must start empty and end `Wood 1 Stone 3 Plank 2 Potion 1 Sword 1 Coin 10 Key 0 Gem 1`, the Coins must arrive at tick 420, the
Gem at 480, the Planks at 299, and no warning or error may be logged. It prints
`TL-DEMO inventory final=Wood_1_Stone_3_Plank_2_Potion_1_Sword_1_Coin_10_Key_0_Gem_1 coinAt=420 gemAt=480 plankAt=299 ...`.
