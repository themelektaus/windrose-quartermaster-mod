"""
Quartermaster - UE5.6 Building-Item Template Generator
======================================================

Run this script ONCE inside the UE5.6 Editor to create a reusable template
asset set for authoring new Quartermaster building items.

How to run
----------
1. Open your UE5.6 project (any blank Blueprint project is fine).
2. Enable plugin "Python Editor Script Plugin"
   (Edit -> Plugins -> search "Python" -> enable -> restart editor).
3. Open the Output Log (Window -> Output Log).
4. In the bottom prompt switch the input mode from "Cmd" to "Python".
5. Paste this command (adjust path to where you cloned the repo):

       exec(open(r"E:\\Windrose\\Mods\\Quartermaster\\Tools\\UeBuildingItem\\create_template.py").read())

   Alternatively use "Tools -> Run Python Script..." and pick this file.

What it creates
---------------
Folder /Game/Quartermaster/Template/ in your project, containing:

  BP_QmBuildingItem      - Blueprint class (parent: PrimaryDataAsset)
                           You add the visible properties to it once
                           (see README.md, Section "Properties einrichten").
  DA_Template_QmItem     - DataAsset instance of BP_QmBuildingItem.
                           This is your "starting point" - duplicate it
                           for every new mod item.
  M_Template_QmItem      - Empty Material as placeholder.
                           Open + edit OR replace with your own.

Idempotent: re-running the script is safe, existing assets are kept.
"""

import unreal

TEMPLATE_DIR    = "/Game/Quartermaster/Template"
BP_NAME         = "BP_QmBuildingItem"
DA_NAME         = "DA_Template_QmItem"
MAT_NAME        = "M_Template_QmItem"


def log(msg):
    unreal.log("[Quartermaster] " + msg)


def warn(msg):
    unreal.log_warning("[Quartermaster] " + msg)


def err(msg):
    unreal.log_error("[Quartermaster] " + msg)


def ensure_directory(path):
    """Create directory under /Game/ if missing. UE auto-creates parents."""
    if unreal.EditorAssetLibrary.does_directory_exist(path):
        log("directory exists: " + path)
        return
    if unreal.EditorAssetLibrary.make_directory(path):
        log("created directory: " + path)
    else:
        err("could not create directory: " + path)


def asset_path(name):
    return TEMPLATE_DIR + "/" + name


def create_blueprint_class():
    """Create BP_QmBuildingItem if missing. Parent class: PrimaryDataAsset.
    Properties (Mesh/Material/Icon/DisplayName/Description) are NOT added
    here because the Python API for BP variable creation is unstable across
    UE versions - we ask the user to add them manually via the BP editor."""
    full = asset_path(BP_NAME)
    if unreal.EditorAssetLibrary.does_asset_exist(full):
        log("blueprint exists, keeping: " + full)
        return unreal.EditorAssetLibrary.load_asset(full)

    factory = unreal.BlueprintFactory()
    factory.set_editor_property("parent_class", unreal.PrimaryDataAsset)

    tools = unreal.AssetToolsHelpers.get_asset_tools()
    bp = tools.create_asset(BP_NAME, TEMPLATE_DIR, unreal.Blueprint, factory)
    if bp is None:
        err("failed to create blueprint: " + full)
        return None

    unreal.EditorAssetLibrary.save_asset(full)
    log("created blueprint: " + full + "  (parent=PrimaryDataAsset)")
    return bp


def create_data_asset(bp):
    """Create DA_Template_QmItem as instance of BP_QmBuildingItem if missing."""
    full = asset_path(DA_NAME)
    if unreal.EditorAssetLibrary.does_asset_exist(full):
        log("data asset exists, keeping: " + full)
        return unreal.EditorAssetLibrary.load_asset(full)

    if bp is None:
        warn("skipping data asset because blueprint missing")
        return None

    # The generated class lives on the Blueprint's GeneratedClass property.
    # `get_editor_property` is the version-portable accessor; some UE builds
    # expose `.generated_class()` as a method, others as an attribute.
    gen_class = None
    try:
        gen_class = bp.get_editor_property("generated_class")
    except Exception:
        try:
            gen_class = bp.generated_class()  # method form on some builds
        except Exception:
            pass
    if gen_class is None:
        err("blueprint has no generated class yet - reopen project and re-run")
        return None

    factory = unreal.DataAssetFactory()
    factory.set_editor_property("data_asset_class", gen_class)

    tools = unreal.AssetToolsHelpers.get_asset_tools()
    da = tools.create_asset(DA_NAME, TEMPLATE_DIR, gen_class, factory)
    if da is None:
        err("failed to create data asset: " + full)
        return None

    unreal.EditorAssetLibrary.save_asset(full)
    log("created data asset: " + full)
    return da


def create_placeholder_material():
    """Create M_Template_QmItem as empty Material. User replaces with their
    own material later - this just gives a slot to drag-link from the DA."""
    full = asset_path(MAT_NAME)
    if unreal.EditorAssetLibrary.does_asset_exist(full):
        log("material exists, keeping: " + full)
        return unreal.EditorAssetLibrary.load_asset(full)

    factory = unreal.MaterialFactoryNew()
    tools = unreal.AssetToolsHelpers.get_asset_tools()
    mat = tools.create_asset(MAT_NAME, TEMPLATE_DIR, unreal.Material, factory)
    if mat is None:
        err("failed to create material: " + full)
        return None

    unreal.EditorAssetLibrary.save_asset(full)
    log("created material: " + full)
    return mat


def banner_next_steps():
    log("=" * 60)
    log("Template set ready under " + TEMPLATE_DIR)
    log("Next steps:")
    log("  1) Open BP_QmBuildingItem -> add variables (Mesh, Material,")
    log("     Icon, DisplayName, Description) - see README section")
    log("     'Properties einrichten'.")
    log("  2) Compile + Save the Blueprint (Ctrl+F7 then Ctrl+S).")
    log("  3) Right-click DA_Template_QmItem -> Duplicate ->")
    log("     name it DA_BI_<YourItem>_01.")
    log("  4) Open the duplicate, fill in the references, save.")
    log("  5) Continue with Howto_AuthorBuildingItem.md Schritt 9 (Cook).")
    log("=" * 60)


def main():
    log("=== Quartermaster template generator ===")
    ensure_directory(TEMPLATE_DIR)
    bp  = create_blueprint_class()
    da  = create_data_asset(bp)
    mat = create_placeholder_material()
    banner_next_steps()


# Allow both `python script.py` style and exec() invocation from the
# Output Log Python prompt.
main()
