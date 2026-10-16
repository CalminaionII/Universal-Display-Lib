# Universal Display Lib - guide for modders

The full modder reference for Universal Display Lib (the ModDB page only has room for the short version). Questions and reports: the Universal Display Lib page on mods.vintagestory.at.

## For Modders

Give your own blocks display surfaces, let the lib work out every box from your model, and tell it how items should sit - with JSON and your shape file, no code.

### Quick Start

**1.** **Depend on the lib**

Add it to your `modinfo.json`, because your blocks use its block and block entity classes:

```json
"dependencies": { "universaldisplaylib": "1.2.3" }
```

**2.** **Mark a surface in your shape**

In Model Creator, add one cube where items should stand and name it, for example `psurface0-w16-h4-d16-box`. The name is the whole setup - see *Display Surfaces* below.

**3.** **Copy the example below into a blocktype**

Change **code** and the shape path to yours, and list the surface cube in **ignoreElements** so the game does not draw it.

**4.** **Load a world**

Place your block and put an item on it. If a surface name is wrong, the log names the shape element and what it expected (`[UniversalDisplayLib] ... looks like a psurface but ...`).

**Minimal example** - a block with one surface, nothing that opens:

```json
{
  "code": "displaybox",
  "class": "BlockUniversalDisplay",
  "entityClass": "BEUniversalDisplay",
  "shape": {
    "base": "yourmod:block/displaybox",
    "ignoreElements": [ "psurface0-w16-h4-d16-box" ]
  },
  "behaviors": [
    { "name": "DisplaySurfaces" }
  ],
  "attributes": {
    "canOpen": false
  },
  "creativeinventory": { "general": [ "*" ] },
  "drawtype": "JSON",
  "lightAbsorption": 0,
  "sideopaque": { "all": false },
  "sidesolid": { "all": false }
}
```

**No boxes to write.** A block never needs a selection or collision box: they are measured from your model when the world loads - each part that opens gets its own outline that moves with it, groups you name `Static...` get one outline each, and everything else is solid. A model taller than one block is placed as one block with invisible fillers above it, and a part that swings out of the block is solid and clickable where it goes (Universal Display Lib 1.2.0 and later).

#### Show the full example with every field

A shop window with a leaf that swings out and a drawer under the sill. Only **code**, **class** and **entityClass** are required. Each field is explained in the reference below.

```json
{
  "code": "shopwindow",
  "class": "BlockUniversalDisplay",
  "entityClass": "BEUniversalDisplay",
  "variantgroups": [ { "code": "state", "states": [ "closed", "open" ] } ],
  "shape": {
    "base": "yourmod:block/shopwindow",
    "ignoreElements": [ "psurface0-w16-h7-d7-windowdisplay" ]
  },
  "behaviors": [
    { "name": "DisplaySurfaces" },
    { "name": "UDAutoBoxes", "properties": {
        "panes": [ { "pane": 0, "fallback": { "animOpen": "open90", "animClose": "close90" } } ] } },
    { "name": "UDMaterials", "properties": {
        "slots": { "wood": { "frame": "planks" }, "glass": { "glass": "pane" } },
        "fixed": { "handle": "game:block/metal/ingot/brass" },
        "describe": [ "wood", "glass" ] } }
  ],
  "entityBehaviors": [ { "name": "UDMaterials" } ],
  "attributes": {
    "canOpen": true,
    "animatedElements": [ "Leaf", "Drawer" ],
    "frameBoxGroups": [
      { "animOpen": "open", "animClose": "close" },
      {
        "animOpen": "pullout", "animClose": "pushin",
        "openLabel": "Pull out", "closeLabel": "Push in",
        "openSound": "game:sounds/block/chestopen", "closeSound": "game:sounds/block/chestclose",
        "animSpeed": 2,
        "autoCloseSeconds": 10,
        "openState": "Pulled out", "closedState": "Pushed in",
        "boxStaysShut": true,
        "sealsRoom": false
      }
    ],
    "displaySounds": {
      "toggle": "game:sounds/effect/swoosh",
      "toggleSolid": "game:sounds/block/door",
      "swap": "game:sounds/block/planks",
      "rain": "game:sounds/environment/rain"
    },
    "surfaceFacingDeg": { "0": 0 },
    "showSurfacesWhenStacked": [ 0 ],
    "noPlacementJitter": true,
    "lockChiselledSurfaces": [ 0 ],
    "wrenchSwapTo": "yourmod:shopwindowwide-{state}",
    "dropsAs": "yourmod:shopwindow-closed"
  },
  "creativeInventoryStacksByType": {
    "*-closed": [ {
      "tabs": [ "general" ],
      "stacks": [ { "type": "block", "code": "shopwindow-closed",
                    "attributes": { "types": { "wood": "oak", "glass": "plain" } } } ]
    } ]
  },
  "drawtype": "JSON",
  "lightAbsorption": 0,
  "sideopaque": { "all": false }
}
```

### Field Reference

Everything not listed here is an ordinary block setting and works as it does on any block. To change one of Window Display's blocks, patch its blocktype as you would any file.

| **Required** |  |
|---|---|
| `class` | `BlockUniversalDisplay` - the lib's block. It places, swaps and drops the block. |
| `entityClass` | `BEUniversalDisplay` - the lib's block entity. It holds the items, plays the animations, works out the boxes and saves everything. Use both, always together. |
| **The model** |  |
| `shape` | Your model, with every `psurface` cube named in `ignoreElements` - otherwise the cube is drawn in the game. The lib reads the markers from the shape whether they are drawn or not. |
| `variantgroups` | Optional. A `state` group of `closed` / `open` for a block with a pane that seals the room, like a window. Opening a pane swaps the block to its `open` state, so the game's room check sees the gap and the open sides stop being solid. A block with no such pane needs no variants. |
| **Behaviours - all optional** |  |
| `DisplaySurfaces` | Turns the `psurface` markers into places items can be put. Leave it out for a block with no surfaces (an External Shutter). Its properties `maxXDivisions` / `maxZDivisions` (32 by default) cap how finely a surface is divided - you will rarely need them. |
| `UDAutoBoxes` | Only for a pane that might not have room to swing all the way. `panes` is a list of `{ "pane": <number>, "fallback": { "animOpen": ..., "animClose": ... } }` - **`pane` is required**, an entry without it is ignored. When the full swing is blocked, the pane plays this smaller pose from your shape instead. Without a fallback, a blocked pane stays shut and the player is told why. |
| `UDMaterials` | One block in every wood, glass or other material, instead of one block per material. See *Materials* below. Needs the entity behaviour of the same name too. |
| **Panes - block attributes, all optional** |  |
| `canOpen` | True if anything on the block opens. False for a block where nothing does. |
| `animatedElements` | The shape elements your animations move. Only these are animated each frame - the rest of the block is baked in with the terrain, which keeps it cheap. |
| `frameBoxGroups` | One entry per pane, pane 0 first. Each names the pane's two animations from your shape and any of the options in the next table. The lib measures each pane's box from what its open animation moves, so there are no box sizes to write. |
| **Sounds - all optional** |  |
| `displaySounds` | Sounds for the whole block. `toggle` - a pane opening or shutting (default `game:sounds/effect/swoosh`); `toggleSolid` - the same for panes with `sealsRoom` false (default `game:sounds/block/door`); `swap` - the wrench swap (default the planks sound); `rain` - rain heard through an open pane, silent unless you name one. A path without a domain is your own mod's. Every player's own open and close volume setting applies. |
| **Items on the surfaces - all optional** |  |
| `surfaceFacingDeg` | Which way items on a surface face, by surface number: `{ "0": 90 }`. Only needed if the lib's own guess is wrong for your model - it works it out from the side of the block the surface sits on. |
| `showSurfacesWhenStacked` | When your block stands on another Universal Display block, its surfaces close, because they are usually obstructed. List the surface numbers that should stay usable: `[ 0 ]`. Left out: all of them close. |
| `noPlacementJitter` | True: items on this block sit square, without the small random turn. Players can also switch the turn off for everything, and an item can opt out with `randYRotAngle: 0`. |
| `lockChiselledSurfaces` | Surface numbers. The FIRST chiselled block put on one of these surfaces stays fixed - it cannot be taken, turned or nudged, and drops only when the whole block is broken. Anything put on that surface afterwards stays takeable. For designs where a chiselled piece is part of the structure. |
| **Swapping and drops - all optional** |  |
| `wrenchSwapTo` | Sprint + right-click with a wrench turns the block into this one, in place, keeping its facing - only while it is empty and every pane is shut. `{state}` (or any of your variant groups) is filled from the current block. **Write the domain** - `yourmod:...` - or it looks for the block in `game:`. For a ring of more than two, point each block at the next one. A list is a fallback list (the first that exists is used), not a cycle. |
| `dropsAs` | What the block drops when broken - for a block that should only ever drop its closed or plain version. Domain included, as for `wrenchSwapTo`; `{state}` works here too. |
| **Old names, still read** |  |
| `isWindow` | The old name for `sealsRoom`. |
| `legacySegment` | Marks an old upper section of a chained tall window, so worlds made before 1.2.0 convert it into the new one-block window. Not for new blocks. |

Each entry in `frameBoxGroups`:

| **Required** |  |
|---|---|
| `animOpen` / `animClose` | The animation codes in your shape that open and shut this pane - the animation's **code**, not its name. A code that does not exist fails silently: the pane just never moves. |
| **Optional** |  |
| `openLabel` / `closeLabel` | What the interaction help says a click does - a lang key or plain text, such as "Pull out" / "Push in". Default "Open" / "Close" (1.2.2 and later). |
| `openSound` / `closeSound` | This pane's own sounds instead of the block's `displaySounds` (1.2.2 and later). |
| `animSpeed` | This pane's speed, times the player's own animation speed setting - 0.5 for a heavy door, 2 for a drawer. Default 1 (1.2.2 and later). |
| `autoCloseSeconds` | The pane shuts by itself this many seconds after it is opened. It waits while something open is in its way. Default 0, never (1.2.2 and later). |
| `openState` / `closedState` | The block info's words for this pane's state instead of "Open" / "Closed" (1.2.2 and later). |
| `boxStaysShut` | True: the pane keeps its shut box while it is open, so it swings out with nothing to bump into or click - the awning and hopper do this. Room to open is still checked along the real swing. |
| `sealsRoom` | True by default: the shut pane seals the room, and opening it swaps the block to `open`. False for a part that is not a gap in the wall - a drawer, an interior door, a shutter over a solid panel. It then uses the `toggleSolid` sound and never swaps the block. |

### Display Surfaces

A surface is one cube in the block's shape, and its name is the configuration:

```json
{ "name": "psurface0-w16-h4-d16-box", "from": [ 0, 10, 0 ], "to": [ 16, 14, 16 ] }
```

| `psurface0` | The surface's number. One block can carry several, each used on its own - an inner sill, an outer sill, a shelf. Other settings (`surfaceFacingDeg`, `showSurfacesWhenStacked`, `lockChiselledSurfaces`) refer to surfaces by this number. |
|---|---|
| `w16` `h4` `d16` | Width, headroom and depth for items, in voxels. The headroom limits how high items can stack. |
| `box` | The display category - any name you like. Optional: left off, it is `shelf`, vanilla's own. |

- Items stand on the cube's floor, at its `from` height. Draw it where items should rest.

- Items sit on a half-voxel grid, centred where the player clicks, and must fit wholly inside the surface.

- **The category keeps your data separate.** A surface ending `-box` reads `displayable.box` off the item, so the same item can sit one way on your block and another way on a window or a vanilla shelf, without any of them overwriting each other.

- **Items nobody patched still work.** With no entry for your category, the lib falls back to the item's `displayable.shelf`, then `shelvable`, then a plant's potted form - most of the game can go on your block before you write a single patch.

### Boxes From The Model

You do not write box sizes. When the world loads, the lib measures the block's own shape:

| **Panes** | Everything a pane's `animOpen` animation turns or moves is that pane: one outlined, solid box that moves with it. A click on it opens or shuts that pane. |
|---|---|
| **Static groups** | Name a shape element `Static...` (any case - `Static1`, `StaticGlass`) and everything inside it gets one outlined, solid box. For a fixed part you want aimable, such as fixed glass. A click opens nothing; the wrench and breaking work there. A Static group inside a pane belongs to the pane. |
| **Everything else** | Solid, and stops your aim, with no outline and nothing happening on a click - so a player never aims through a frame at what is behind it (1.2.2 and later; before that, these parts were solid only). A block with no pane, no Static group and no surface has nothing outlined - give it a Static group so players can see what they are aiming at. |
| **Bigger than a block** | A model taller than one block is placed as one block with invisible fillers above it. Placing needs room for all of it, and breaking any part breaks all of it. A pane whose animation leaves the block gets a filler where it goes while it is open. Two blocks can share one space, such as two shutters folding into the same gap, as long as their parts do not overlap. |
| **Room to open** | Before a pane opens, its whole swing is checked, not just where it ends; blocked, it plays its `UDAutoBoxes` fallback or stays shut. Before a pane shuts, it checks it is not shutting through something else that is open. |

- Written boxes (`openFrameBox`, `closedFrameBox`, `staticFrameBoxes`, `collisionBoxGroups`) are only used where nothing can be measured, such as an empty model.

- Model Creator saves `"textures": {}` when it re-exports a shape. Put your default textures back after every export, or the block draws blank wherever no material fills it.

### Giving An Item A Display Size

Most items need nothing: an item with a vanilla `displayable.shelf` entry already works, at its own size and stacking. Patch only an item with no display data, or one too big to fit. The lib measures every item from its drawn model, transform applied, so you write how it should look and the lib works out the space it takes.

```json
[
  {
    "op": "addmerge",
    "path": "/attributes",
    "file": "yourmod:itemtypes/lantern.json",
    "value": {
      "displayable": {
        "windowdisplay": {
          "behavior": "Stacking",
          "randYRotAngle": 0,
          "particleOffset": { "x": 0, "y": 0, "z": 0 },
          "transform": {
            "translation": { "x": 0, "y": 0, "z": 0 },
            "rotation": { "x": 0, "y": 0, "z": 0 },
            "origin": { "x": 0.5, "y": 0, "z": 0.5 },
            "scale": 1
          }
        }
      }
    }
  }
]
```

| `transform` | How the item sits: translation, rotation, origin and scale, as in any of the game's transforms. An item must fit inside the surface, so scale a big one down. |
|---|---|
| `behavior` | `"Stacking"` lets items pile on top of each other. Left out, the item does not stack. |
| `randYRotAngle` | The full spread of the small random resting angle, in degrees (30 by default, so up to 15 either way). 0 sits square. |
| `particleOffset` | Switches on the item's flame or smoke while it is displayed, at this spot - in voxels, measured on the unrotated, unscaled model; the lib turns and scales it with the item. Nothing emits without it. |
| `shape` | A different model on display, such as folded clothing. |
| `outline` | `"size"` keeps the outline and the footprint to a written `size` instead of the measured one: `"size": { "width": 3, "height": 5, "length": 3 }`. For crossed-plane plants, whose model fills the whole block. Without `"outline": "size"` a written size is ignored. |

- Any key takes a `ByType` form for per-variant values (`transformByType`, `sizeByType`), as everywhere in the game. The first matching key wins, so put a `*` key last.

- **Use `addmerge`, not `add`.** `add` replaces the item's whole `displayable`, vanilla's shelf entry included, and vanilla's shelves read that. `addmerge` on `/attributes` creates what is missing and keeps what is there.

- An override replaces vanilla's entry for that category wholesale, so restate `behavior` if you want stacking.

- The order the lib looks in: your category (code, then `displayable.<category>`), then `shelf` (code, then `displayable.shelf`), then `shelvable`, then the `universaldisplayable` flag.

- Light comes from the item itself: a lantern lights the room at its own brightness, with nothing to set.

**The easiest way to get the numbers is the editor.** Type `.udedit` and point at an item on any surface: set its transform with every change live, and the finished entry is written to `ModConfig/universaldisplaylib/display-entries.txt`, ready to paste. The **Display Editor Surface** in the creative inventory is a plain slab to tune on without a finished block; it uses its own category, `editorsurface`, so change that key to yours before pasting. To tune for vanilla shelves, tune here and paste the entry under `shelf`.

### Materials

One block in every wood, glass, metal or anything else - the lib's own materials system (1.2.0 and later; recipes by material, the creative and handbook settings and one handbook page per block need 1.2.3; Attribute Rendering Library is no longer needed, and blocks made with it keep their materials).

**Material files.** A list at `assets/<yourmod>/config/universaldisplay/materials/<kind>.json`. The file name is the kind: `wood.json`, `glass.json`, `metal.json`.

```json
[
  { "code": "*", "name": "game:material-{code}", "item": "game:plank-{code}",
    "textures": { "planks": "game:block/wood/planks/{code}1", "debarked": "game:block/wood/debarked/{code}" } },
  { "code": "oak" },
  { "code": "veryaged", "textures": { "planks": "game:block/wood/planks/aged/veryaged1" },
    "overlays": { "glass": [ "game:block/overlay/dust-sparse" ] } }
]
```

| `code` | The material's code, saved on every block and stack. **Never rename it after release.** `*` answers every code not listed, with `{code}` filled in. |
|---|---|
| `name` | Lang key for its name, such as `game:material-{code}` ("Oak"). |
| `item` | The ingredient that means this material in a recipe - a plank, a pane, an ingot. |
| `textures` | Its textures by role (`planks`, `debarked`, `pane` ...). A block's `slots` say which of its shape's textures takes which role. |
| `overlays` | Optional. Overlays this material adds to one of the block's shape textures, whatever fills it - very aged wood dusts the glass. |
| `source` | Optional (1.2.2 and later). The mod the material counts as, when its `item` is in another domain - Pewter's metal is `game:` but belongs to Pewter. Decides what "modded" means below. |
| `listedOnly` | Optional, on `*` (1.2.2 and later). Its `item` pattern answers only for LISTED codes - for a pattern that also matches items that are not materials. |

- An entry that names only a `code` takes everything else from the `*` entry.

- Listing a code puts it in the creative tab, in the file's order. Codes that are not listed still work through `*` (crafted with a plank that fits), they just get no creative stacks.

- Each mod's own files come first for its own blocks, so your names and order can differ from Window Display's. A code another mod defines differently is logged, and your version is used only for your blocks.

**On the block** - `UDMaterials`, both as a block behaviour and an entity behaviour:

| `slots` | Which of your shape's texture codes each kind fills, and with which role: `{ "wood": { "frame": "planks" }, "glass": { "glass": "pane" } }`. |
|---|---|
| `fixed` | Shape textures that never change: `{ "handle": "game:block/metal/ingot/brass" }`. |
| `overlays` | Overlays this block always adds to a shape texture, whatever the material: `{ "frame": [ "game:block/overlay/..." ] }`. |
| `describe` | The kinds named in the tooltip, in order: `[ "wood", "glass" ]` adds "Oak & Plain glass". |
| `creative` | 1.2.2 and later. What the creative tab shows: `{ "vanilla": "all", "modded": "one" }`. `vanilla` (the game's materials): `all` every combination (default), `each` each material once, `written` only the stacks you wrote. `modded` (other mods' materials): `none` (default), `each`, `one` stack per mod, `all`. A plain word sets `vanilla` only. |
| `handbook` | The same choices for the handbook. Left out: what the creative tab shows. |
| `showMaterials` | Per kind, EXACTLY which materials show: codes, `"vanilla"` or a mod id - `{ "wood": [ "vanilla", "vanillavarnished", "sandalwood" ] }`. Combined by the `vanilla` choice. |
| `compactHandbook` | `true`: one handbook page for the block, its icon flipping through its stacks. H on a stack still opens it showing that stack. |
| `handbookMods` | `true`: the page says which mods' materials it can be made with - "Can be made with: Vanilla, Vanilla Varnished" - the installed ones, every kind the block holds. A block made only by stacking blocks that carry their materials (tall windows) gets no line. Lang: `handbook-mods-title`, `handbook-source-game`, `materialkind-name-<kind>`. |

- The block and its stack keep their codes in a `types` tree: `"attributes": { "types": { "wood": "oak", "glass": "plain" } }`. Give each creative list ONE stack - the "written" stack the lib builds the rest from. Showing never changes crafting: a recipe takes every material, shown or not.

- **One recipe per block (1.2.2 and later).** Unnamed wildcards and a letter-to-kind map:

```json
{
  "ingredientPattern": "CBD,BAB,BBB", "width": 3, "height": 3,
  "ingredients": {
    "A": { "type": "item", "code": "windowdisplay:glasspane" },
    "B": { "type": "item", "code": "*:plank-*" },
    "C": { "type": "item", "code": "game:metalnailsandstrips-*" },
    "D": { "type": "item", "code": "game:resin" }
  },
  "copyAttributesFrom": "A",
  "attributes": { "udMaterials": { "B": "wood" } },
  "output": { "type": "block", "code": "windowdisplay:windowdisplay-hopper-closed" }
}
```

  `*:plank-*` with no `"name"` stays ONE recipe that takes every mod's planks. The lib lets it match only when each letter holds one material of its kind (all the planks the same wood) and writes the materials onto what is crafted. Different letters can be different kinds - a frame wood and a shelf wood. `copyAttributesFrom` carries another item's materials across (the glass of the pane). Don't use `"name": "wood"` wildcards for these: the game would make one recipe per plank.

- **A new material is one patch.** `addeach` onto the materials file, with `dependsOn` the mod it comes from - Window Display adds Biodiversity, Vanilla Varnished and Wildcraft Trees woods this way. The recipes already take its planks:

```json
[
  {
    "op": "addeach",
    "path": "/-",
    "file": "windowdisplay:config/universaldisplay/materials/wood.json",
    "dependsOn": [ { "modid": "yourwoodmod" } ],
    "value": [
      { "code": "redcedar", "name": "yourwoodmod:material-redcedar", "item": "yourwoodmod:plank-redcedar",
        "textures": { "planks": "yourwoodmod:block/wood/planks/redcedar1",
                      "debarked": "yourwoodmod:block/wood/debarked/redcedar" } }
    ]
  }
]
```

- **Items can have materials too (1.2.2 and later)** - the same `UDMaterials` behaviour on an item makes one item that comes in every material, drawn in it in the hand, on shelves and in ground piles. Window Display's glass pane is one: `"slots": { "glass": { "glass": "pane" } }`, `"name"` a lang key with `{0}` = the material's name, plus `creative` / `compactHandbook` as on blocks.

- **Handbook pages grouped in JSON** - any block or item, with or without materials: `"attributes": { "handbookGroup": { "by": "types/wood", "title": "yourmod:chair-group", "names": "game:material-{0}" } }` gives one page per wood. `by` is the stack attribute that picks the page (left out: one page for all), `title` a lang key with `{0}`, `names` turns a value into its name.

### For Code Mods

You can hear a pane open or shut, on the server and on each player's game as the change arrives:

```csharp
BEUniversalDisplay.PaneChanged += (be, pane, open) => { /* be.Pos, pane number, open or shut */ };
```

A mistake in your listener is logged and does not stop the block. Unsubscribe in your mod system's `Dispose`, since the event is static.

### Shipping It With Your Mod

**Blocks need the dependency.** A blocktype that uses `BlockUniversalDisplay`, `BEUniversalDisplay` or the lib's behaviours does not load without the lib, so list `universaldisplaylib` in your `modinfo.json` dependencies.

**Item patches do not.** A `displayable` entry is inert for anyone without the lib - the game never reads it - so a mod that only adds display data to items can ship the patches without depending on the lib. Every entry that targets another mod's assets needs `"dependsOn": [ { "modid": "<that mod>" } ]`: without it, players who do not have that mod get an error; with it, the patch is skipped silently. This is how Window Display Patches covers dozens of mods.

- Install on both the server and every client - the lib is needed on both sides.

- Test a pane's room check with something beside it, and a tall block with something above it - those are the two cases a model alone cannot show you.
