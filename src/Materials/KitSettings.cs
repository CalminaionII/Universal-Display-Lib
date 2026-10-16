namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// THE MATERIALS KIT'S SETTINGS - the names that must be different in every mod that carries the kit, so several mods
    /// can run it side by side, and the few switches a LIBRARY needs (a mod that serves other mods' blocks, like Universal
    /// Display Lib). install-kit.py fills them in and keeps them when you update; change them by hand only if you copy the
    /// files yourself.
    /// </summary>
    public static class KitSettings
    {
        /// <summary>The behaviour name your blocktypes and itemtypes use: <c>{ "name": "MyModMaterials" }</c>. Must be unique in the game.</summary>
        public const string BehaviorName = "UDMaterials";

        /// <summary>Where the materials files live: assets/&lt;modid&gt;/config/materials/&lt;kind&gt;.json.</summary>
        public const string Folder = "config/universaldisplay/materials/";

        /// <summary>The recipe attribute naming which grid letter is which kind: <c>"attributes": { "materials": { "F": "wood" } }</c>.</summary>
        public const string RecipeKey = "udMaterials";

        /// <summary>
        /// false (a normal mod): only YOUR mod's materials files, blocks and recipes. true (a library): every mod's - each block
        /// still looks in its own mod's files first, then the shared pool, which keeps the first definition of a code.
        /// </summary>
        public static readonly bool AllDomains = true;

        /// <summary>With AllDomains: the mods whose files load first (after your own) - their definitions win the shared pool.</summary>
        public static readonly string[] FirstDomains = { "windowdisplay" };

        /// <summary>A library's lang domain for the tooltip's kind lines (<c>&lt;domain&gt;:materialkind-&lt;kind&gt;</c>, <c>material-join</c>), tried after the block's own.</summary>
        public const string LangDomain = "universaldisplaylib";

        /// <summary>
        /// true: the kit draws placed blocks, and gives drops and middle-click their materials. false: your own block / block
        /// entity classes do that (they use MaterialTextureSource and BEBehaviorMaterials.CopyTo).
        /// </summary>
        public static readonly bool HandlesPlacedBlocks = false;
    }
}
