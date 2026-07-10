using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

// UI panel, archetype presets, randomizer and item-JSON export for the tool / weapon generator. The geometry
// itself lives in ModelEditor.Weapon.cs; this file is the parameter surface the user drives.
public sealed partial class DebugWindowManager
{
    private void DrawWeaponPanel()
    {
        if (!_weaponWindowOpen) return;
        if (_modelDoc == null)
        {
            ImGui.TextDisabled("Open a shape or create a new one first.");
            return;
        }

        bool changed = false;
        WeaponParams p = _weaponParams;

        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Archetype##weapon-archetype", ref _weaponArchetypeIndex, WeaponArchetypeLabels, WeaponArchetypeLabels.Length))
        {
            ModelApplyWeaponArchetype((WeaponArchetype)_weaponArchetypeIndex);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Apply preset##weapon-preset"))
        {
            ModelApplyWeaponArchetype((WeaponArchetype)_weaponArchetypeIndex);
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset all geometry to this archetype's defaults (keeps name, placement, textures and seed).");

        ImGui.SetNextItemWidth(150f);
        changed |= ImGui.InputInt("Seed##weapon-seed", ref p.Seed);
        ImGui.SameLine();
        if (ImGui.Button("Randomize##weapon-rng"))
        {
            p.Seed++;
            ModelRandomizeWeapon(p);
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Advance the seed and jitter every parameter within sensible ranges. A given seed is reproducible.");

        ImGui.SetNextItemWidth(150f);
        ImGui.InputText("Name##weapon-name", ref p.Name, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputText("Code##weapon-code", ref p.Code, 64);

        changed |= ImGui.DragFloat3("Center##weapon-center", ref p.Center, 0.25f, -256f, 272f, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Item center in shape units (16 = 1 block). Becomes the root group's pivot.");

        List<string> textureCodes = _modelDoc.Textures.Select(texture => texture.Code).ToList();
        ImGui.SetNextItemWidth(150f);
        if (ModelFilteredCombo("Metal##weapon-tex-metal", p.Texture, textureCodes, out string metalPick, allowCustom: true, filterHint: "metal / primary texture"))
        {
            p.Texture = metalPick;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        if (ModelFilteredCombo("Handle##weapon-tex-handle", p.HandleTexture, textureCodes, out string handlePick, allowCustom: true, filterHint: "wood / leather grip"))
        {
            p.HandleTexture = handlePick;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        if (ModelFilteredCombo("Accent##weapon-tex-accent", p.AccentTexture, textureCodes, out string accentPick, allowCustom: true, filterHint: "cord / fuller / gem"))
        {
            p.AccentTexture = accentPick;
            changed = true;
        }

        changed |= ImGui.Checkbox("Auto textures by part##weapon-autotex", ref p.AutoTexture);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Split metal / handle / accent into separate texture codes. Missing codes are created on Create, seeded from the metal texture so each part renders immediately.");
        ImGui.SameLine();
        ImGui.Checkbox("Advanced##weapon-advanced", ref _weaponAdvanced);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reveal the full set of fine-tuning parameters (rotation, scale, detail toggles, item JSON).");

        if (_weaponAdvanced)
        {
            changed |= ImGui.DragFloat3("Rotation##weapon-rotation", ref p.Rotation, 1f, -360f, 360f, "%.1f deg");
            ImGui.SetNextItemWidth(180f);
            changed |= ImGui.SliderFloat("Overall scale##weapon-scale", ref p.UniformScale, 0.1f, 8f, "%.2fx");
        }

        ImGui.Separator();
        changed |= DrawWeaponSections(p);

        if (changed) _weaponPreviewDirty = true;
        if (_weaponPreviewDirty)
        {
            _weaponPreviewDirty = false;
            _weaponPreviewRoot = ModelBuildWeapon(out _weaponPreviewError);
            _weaponPreviewCount = _weaponPreviewRoot == null ? 0 : ModelCreatureElementCount(_weaponPreviewRoot);
        }

        ImGui.Separator();
        if (!string.IsNullOrEmpty(_weaponPreviewError))
        {
            ImGui.TextColored(new NVector4(1f, 0.42f, 0.34f, 1f), _weaponPreviewError);
        }
        else
        {
            if (ModelIsMeshLibMode)
            {
                (int vertices, int faces) = ModelGeneratedMeshCounts(_weaponPreviewRoot);
                ImGui.TextUnformatted($"{_weaponPreviewCount} mesh element(s), {vertices} vertices, {faces} faces. Blue ghost shows the result.");
            }
            else
            {
                ImGui.TextUnformatted($"{_weaponPreviewCount} element(s) / {WeaponMaxElements} max. Blue ghost shows the result.");
            }
        }

        bool canCreate = _weaponPreviewRoot != null && string.IsNullOrEmpty(_weaponPreviewError);
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create##weapon-create")) ModelCommitWeapon();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add the weapon to the shape, grouped under one parent element (one undo step).");
        ImGui.SameLine();
        if (ImGui.Button("Create & animate##weapon-create-animate"))
        {
            ModelCommitWeapon();
            ModelAnimateCurrentShape();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add the weapon, save the shape, and open it in the animation editor's Shapes tab.");
        ImGui.SameLine();
        if (ImGui.Button("Export item JSON##weapon-export")) ModelWeaponExportItem();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Write a drop-in itemtype JSON (class, tool, attack power/range, durability, tooltier, shape path) to the InGameDevTools authored-assets folder. Save the shape first so the shape path resolves.");
        if (!canCreate) ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(_weaponStatus))
        {
            ImGui.TextColored(new NVector4(0.62f, 0.8f, 0.62f, 1f), _weaponStatus);
        }
    }

    private bool DrawWeaponSections(WeaponParams p)
    {
        bool changed = false;
        bool adv = _weaponAdvanced;

        if (ImGui.CollapsingHeader("Grip & haft##weapon-grip", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.SliderFloat("Length##weapon-grip-len", ref p.GripLength, 1.5f, 48f, "%.1f");
            changed |= ImGui.SliderFloat("Width (Y)##weapon-grip-w", ref p.GripWidth, 0.4f, 4f, "%.2f");
            changed |= ImGui.SliderFloat("Depth (Z)##weapon-grip-d", ref p.GripDepth, 0.4f, 4f, "%.2f");
            changed |= ImGui.Combo("Section##weapon-grip-sect", ref p.GripSection, WeaponGripSectionLabels, WeaponGripSectionLabels.Length);
            if (adv)
            {
                changed |= ImGui.SliderFloat("Taper##weapon-grip-taper", ref p.GripTaper, 0.3f, 2f, "%.2f");
                changed |= ImGui.SliderFloat("Swell / waist##weapon-grip-swell", ref p.GripSwell, -40f, 60f, "%.0f%%");
                changed |= ImGui.Checkbox("Collar / ferrule##weapon-grip-collar", ref p.GripCollar);
            }
            changed |= ImGui.SliderInt("Wrap rings##weapon-grip-wrap", ref p.WrapRings, 0, 16);
            if (p.WrapRings > 0) changed |= ImGui.SliderFloat("Wrap size##weapon-grip-wrapsize", ref p.WrapSize, 0.1f, 1.5f, "%.2f");
            if (adv)
            {
                changed |= ImGui.SliderInt("Langets##weapon-grip-langets", ref p.Langets, 0, 4);
                if (p.Langets > 0) changed |= ImGui.SliderFloat("Langet length##weapon-grip-langetlen", ref p.LangetLength, 1f, 10f, "%.1f");
            }
        }

        if (ImGui.CollapsingHeader("Guard##weapon-guard"))
        {
            changed |= ImGui.Combo("Style##weapon-guard-style", ref p.GuardStyle, WeaponGuardStyleLabels, WeaponGuardStyleLabels.Length);
            if ((WeaponGuardStyle)p.GuardStyle != WeaponGuardStyle.None)
            {
                changed |= ImGui.SliderFloat("Width##weapon-guard-w", ref p.GuardWidth, 0.5f, 14f, "%.1f");
                changed |= ImGui.SliderFloat("Depth##weapon-guard-d", ref p.GuardDepth, 0.3f, 6f, "%.1f");
                changed |= ImGui.SliderFloat("Thickness##weapon-guard-t", ref p.GuardThickness, 0.2f, 3f, "%.2f");
                if ((WeaponGuardStyle)p.GuardStyle == WeaponGuardStyle.Quillons)
                    changed |= ImGui.SliderFloat("Quillon droop##weapon-guard-droop", ref p.QuillonDroop, -45f, 45f, "%.0f deg");
                if ((WeaponGuardStyle)p.GuardStyle == WeaponGuardStyle.Basket)
                    changed |= ImGui.SliderInt("Basket bars##weapon-guard-bars", ref p.BasketBars, 1, 6);
                if (adv)
                {
                    changed |= ImGui.SliderFloat("Forward offset##weapon-guard-fwd", ref p.GuardForward, -3f, 3f, "%.2f");
                    changed |= ImGui.Checkbox("Cup plate##weapon-guard-cup", ref p.GuardCup);
                }
            }
        }

        if (ImGui.CollapsingHeader("Pommel / butt##weapon-pommel"))
        {
            changed |= ImGui.Combo("Style##weapon-pommel-style", ref p.PommelStyle, WeaponPommelStyleLabels, WeaponPommelStyleLabels.Length);
            if ((WeaponPommelStyle)p.PommelStyle != WeaponPommelStyle.None)
            {
                changed |= ImGui.SliderFloat("Size##weapon-pommel-size", ref p.PommelSize, 0.4f, 5f, "%.2f");
                changed |= ImGui.SliderFloat("Length##weapon-pommel-len", ref p.PommelLength, 0.4f, 5f, "%.2f");
                changed |= ImGui.Checkbox("Peen button##weapon-pommel-peen", ref p.PommelPeen);
            }
        }

        if (ImGui.CollapsingHeader("Head##weapon-head", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(220f);
            changed |= ImGui.Combo("Type##weapon-head-type", ref p.HeadType, WeaponHeadTypeLabels, WeaponHeadTypeLabels.Length);
            changed |= DrawWeaponHeadParams(p, adv);
        }

        if (adv && ImGui.CollapsingHeader("Item JSON##weapon-item"))
        {
            changed |= ImGui.InputText("Tool type##weapon-item-tool", ref p.ToolType, 32);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The 'tool' string written into the item (e.g. sword, axe, pickaxe, spear, bow).");
            changed |= ImGui.InputText("Damage type##weapon-item-dmg", ref p.DamageType, 32);
            changed |= ImGui.SliderFloat("Attack power##weapon-item-power", ref p.AttackPower, 0f, 30f, "%.1f");
            changed |= ImGui.SliderFloat("Attack range##weapon-item-range", ref p.AttackRange, 0.5f, 6f, "%.2f");
            changed |= ImGui.SliderInt("Durability##weapon-item-dura", ref p.Durability, 0, 5000);
            changed |= ImGui.SliderInt("Tool tier##weapon-item-tier", ref p.ToolTier, 0, 12);
            changed |= ImGui.SliderFloat("Mining speed##weapon-item-mine", ref p.MiningSpeed, 0f, 12f, "%.1f");
        }

        return changed;
    }

    private bool DrawWeaponHeadParams(WeaponParams p, bool adv)
    {
        bool changed = false;
        switch ((WeaponHeadType)p.HeadType)
        {
            case WeaponHeadType.Blade:
                changed |= ImGui.SliderInt("Segments##wpn-blade-seg", ref p.BladeSegments, 1, 12);
                changed |= ImGui.SliderFloat("Length##wpn-blade-len", ref p.BladeLength, 2f, 48f, "%.1f");
                changed |= ImGui.SliderFloat("Base width##wpn-blade-bw", ref p.BladeBaseWidth, 0.4f, 8f, "%.2f");
                changed |= ImGui.SliderFloat("Tip width##wpn-blade-tw", ref p.BladeTipWidth, 0.1f, 8f, "%.2f");
                changed |= ImGui.SliderFloat("Spine thickness##wpn-blade-th", ref p.BladeThickness, 0.1f, 3f, "%.2f");
                changed |= ImGui.SliderFloat("Tip thickness##wpn-blade-tth", ref p.BladeTipThickness, 0.08f, 3f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Distal taper: how thin the spine gets toward the point.");
                changed |= ImGui.SliderFloat("Curve (deg/seg)##wpn-blade-curve", ref p.BladeCurve, -20f, 20f, "%.1f");
                changed |= ImGui.Combo("Cross-section##wpn-blade-sect", ref p.BladeSection, WeaponBladeSectionLabels, WeaponBladeSectionLabels.Length);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("The grind: the spine stays thick and the edge(s) thin out. Single = chisel/sabre; double/diamond = symmetric.");
                changed |= ImGui.Combo("Tip##wpn-blade-tip", ref p.BladeTip, WeaponBladeTipLabels, WeaponBladeTipLabels.Length);
                changed |= ImGui.SliderFloat("Tip length##wpn-blade-tiplen", ref p.BladeTipLength, 0.4f, 8f, "%.1f");
                if (adv)
                {
                    changed |= ImGui.Checkbox("Bevel facets##wpn-blade-bev", ref p.BladeBevels);
                    if (p.BladeBevels)
                    {
                        changed |= ImGui.SliderFloat("Bevel width##wpn-blade-bw2", ref p.BladeBevelWidth, 0.1f, 0.9f, "%.2f");
                        changed |= ImGui.SliderFloat("Bevel angle##wpn-blade-ba", ref p.BladeBevelAngle, 5f, 45f, "%.0f deg");
                    }
                    changed |= ImGui.SliderFloat("Edge thickness##wpn-blade-et", ref p.BladeEdgeThickness, 0.15f, 1f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edge thickness as a fraction of the spine thickness (the thin core the grind leaves).");
                    changed |= ImGui.Combo("Spine##wpn-blade-spine", ref p.BladeSpine, WeaponBladeSpineLabels, WeaponBladeSpineLabels.Length);
                    if ((WeaponBladeSpine)p.BladeSpine == WeaponBladeSpine.FalseEdge)
                        changed |= ImGui.SliderFloat("Swedge start##wpn-blade-sf", ref p.BladeSpineFraction, 0f, 1f, "%.2f");
                    changed |= ImGui.Checkbox("Double edge##wpn-blade-de", ref p.BladeDoubleEdge);
                    ImGui.SameLine(); changed |= ImGui.Checkbox("Fuller##wpn-blade-fl", ref p.BladeFuller);
                    if (p.BladeFuller) changed |= ImGui.SliderFloat("Fuller width##wpn-blade-flw", ref p.BladeFullerWidth, 0.1f, 0.8f, "%.2f");
                    changed |= ImGui.Checkbox("Midrib##wpn-blade-mr", ref p.BladeMidrib);
                    ImGui.SameLine(); changed |= ImGui.Checkbox("Ricasso##wpn-blade-ric", ref p.Ricasso);
                    changed |= ImGui.SliderInt("Serrations##wpn-blade-serr", ref p.BladeSerrations, 0, 14);
                }
                break;

            case WeaponHeadType.Axe:
                changed |= ImGui.SliderFloat("Bit width##wpn-axe-bw", ref p.AxeBitWidth, 1f, 16f, "%.1f");
                changed |= ImGui.SliderFloat("Bit depth##wpn-axe-bd", ref p.AxeBitDepth, 1f, 14f, "%.1f");
                changed |= ImGui.SliderFloat("Bit thickness##wpn-axe-bt", ref p.AxeBitThickness, 0.3f, 4f, "%.2f");
                changed |= ImGui.SliderFloat("Beard##wpn-axe-beard", ref p.AxeBeard, 0f, 8f, "%.1f");
                changed |= ImGui.SliderFloat("Top horn##wpn-axe-tophorn", ref p.AxeTopHorn, 0f, 8f, "%.1f");
                changed |= ImGui.SliderFloat("Edge sweep##wpn-axe-sweep", ref p.AxeSweep, -30f, 30f, "%.0f deg");
                changed |= ImGui.Combo("Edge profile##wpn-axe-edge", ref p.AxeEdgeProfile, WeaponEdgeProfileLabels, WeaponEdgeProfileLabels.Length);
                changed |= ImGui.SliderFloat("Head position##wpn-axe-pos", ref p.AxeHeadPos, 0.3f, 1f, "%.2f");
                changed |= ImGui.Checkbox("Double bit##wpn-axe-double", ref p.AxeDoubleBit);
                if (!p.AxeDoubleBit)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.Combo("Back##wpn-axe-back", ref p.AxeBack, WeaponAxeBackLabels, WeaponAxeBackLabels.Length);
                    if ((WeaponAxeBack)p.AxeBack != WeaponAxeBack.None)
                        changed |= ImGui.SliderFloat("Back length##wpn-axe-backlen", ref p.AxeBackLength, 0.5f, 10f, "%.1f");
                }
                if (adv)
                {
                    changed |= ImGui.Checkbox("Raised eye / socket##wpn-axe-eye", ref p.AxeEye);
                    ImGui.SameLine(); changed |= ImGui.Checkbox("Cheeks##wpn-axe-cheeks", ref p.AxeCheeks);
                }
                break;

            case WeaponHeadType.Hammer:
                changed |= ImGui.SliderFloat("Head length##wpn-ham-l", ref p.HammerHeadLength, 1f, 10f, "%.1f");
                changed |= ImGui.SliderFloat("Head width##wpn-ham-w", ref p.HammerHeadWidth, 1f, 10f, "%.1f");
                changed |= ImGui.SliderFloat("Head height##wpn-ham-h", ref p.HammerHeadHeight, 1f, 10f, "%.1f");
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Back##wpn-ham-back", ref p.HammerBack, WeaponHammerBackLabels, WeaponHammerBackLabels.Length);
                if ((WeaponHammerBack)p.HammerBack != WeaponHammerBack.Flat)
                    changed |= ImGui.SliderFloat("Back length##wpn-ham-backlen", ref p.HammerBackLength, 0.5f, 10f, "%.1f");
                changed |= ImGui.Combo("Face shape##wpn-ham-face", ref p.HammerFaceShape, WeaponFaceShapeLabels, WeaponFaceShapeLabels.Length);
                if (adv)
                {
                    changed |= ImGui.SliderFloat("Face bevel##wpn-ham-bevel", ref p.HammerFaceBevel, 0f, 2f, "%.2f");
                    changed |= ImGui.Checkbox("Reinforced collar##wpn-ham-reinf", ref p.HammerReinforced);
                    ImGui.SameLine(); changed |= ImGui.Checkbox("Cheeks##wpn-ham-cheeks", ref p.HammerCheeks);
                }
                break;

            case WeaponHeadType.Mace:
                changed |= ImGui.Combo("Head form##wpn-mace-form", ref p.MaceHead, WeaponMaceHeadLabels, WeaponMaceHeadLabels.Length);
                changed |= ImGui.SliderFloat("Head size##wpn-mace-size", ref p.MaceHeadSize, 1f, 8f, "%.1f");
                changed |= ImGui.SliderInt("Flange count##wpn-mace-count", ref p.FlangeCount, 0, 12);
                changed |= ImGui.Combo("Flange type##wpn-mace-type", ref p.FlangeType, WeaponFlangeTypeLabels, WeaponFlangeTypeLabels.Length);
                changed |= ImGui.SliderFloat("Flange length##wpn-mace-flen", ref p.FlangeLength, 0.3f, 6f, "%.1f");
                changed |= ImGui.SliderFloat("Flange width##wpn-mace-fwid", ref p.FlangeWidth, 0.2f, 4f, "%.1f");
                if (adv)
                {
                    changed |= ImGui.SliderInt("Flange rows##wpn-mace-rows", ref p.FlangeRows, 1, 4);
                    if (p.FlangeRows > 1) changed |= ImGui.SliderFloat("Flange twist##wpn-mace-twist", ref p.FlangeTwist, -45f, 45f, "%.0f deg");
                    changed |= ImGui.Checkbox("Neck collar##wpn-mace-neck", ref p.MaceNeck);
                }
                changed |= ImGui.Checkbox("Top spike##wpn-mace-top", ref p.MaceTopSpike);
                if (p.MaceTopSpike) changed |= ImGui.SliderFloat("Top spike length##wpn-mace-toplen", ref p.MaceTopSpikeLength, 0.6f, 8f, "%.1f");
                break;

            case WeaponHeadType.Spear:
                changed |= ImGui.SliderFloat("Socket length##wpn-spear-sock", ref p.SocketLength, 0.5f, 8f, "%.1f");
                changed |= ImGui.Checkbox("Curved blade (naginata/glaive)##wpn-spear-curved", ref p.SpearCurvedBlade);
                if (!p.SpearCurvedBlade)
                {
                    changed |= ImGui.Combo("Head shape##wpn-spear-shape", ref p.SpearHead, WeaponSpearHeadLabels, WeaponSpearHeadLabels.Length);
                    changed |= ImGui.SliderFloat("Head length##wpn-spear-len", ref p.SpearHeadLength, 1f, 16f, "%.1f");
                    changed |= ImGui.SliderFloat("Head width##wpn-spear-wid", ref p.SpearHeadWidth, 0.4f, 8f, "%.1f");
                    changed |= ImGui.SliderInt("Prongs##wpn-spear-prongs", ref p.SpearProngs, 1, 5);
                    if (p.SpearProngs > 1) changed |= ImGui.SliderFloat("Prong spread##wpn-spear-spread", ref p.SpearProngSpread, 0.5f, 6f, "%.1f");
                    if (adv) changed |= ImGui.Checkbox("Midrib##wpn-spear-rib", ref p.SpearMidrib);
                }
                else
                {
                    changed |= ImGui.SliderFloat("Blade length##wpn-spear-bl", ref p.SpearHeadLength, 2f, 14f, "%.1f");
                    changed |= ImGui.SliderFloat("Blade curve##wpn-spear-bc", ref p.BladeCurve, 0f, 20f, "%.1f");
                }
                changed |= ImGui.Checkbox("Wings/lugs##wpn-spear-wings", ref p.SpearWings);
                ImGui.SameLine(); changed |= ImGui.Checkbox("Side axe##wpn-spear-axe", ref p.SpearSideAxe);
                ImGui.SameLine(); changed |= ImGui.Checkbox("Back hook##wpn-spear-hook", ref p.SpearBackHook);
                if (p.SpearBackHook) changed |= ImGui.SliderFloat("Hook length##wpn-spear-hooklen", ref p.SpearBackHookLength, 1f, 10f, "%.1f");
                if (adv)
                {
                    changed |= ImGui.Checkbox("Fuller##wpn-spear-fuller", ref p.SpearFuller);
                    ImGui.SameLine(); ImGui.SetNextItemWidth(120f);
                    changed |= ImGui.SliderInt("Socket rings##wpn-spear-rings", ref p.SocketRings, 0, 5);
                }
                break;

            case WeaponHeadType.Pick:
                changed |= ImGui.SliderFloat("Spike length##wpn-pick-len", ref p.PickSpikeLength, 1f, 14f, "%.1f");
                changed |= ImGui.SliderFloat("Spike curve##wpn-pick-curve", ref p.PickSpikeCurve, -30f, 30f, "%.0f deg");
                changed |= ImGui.SliderInt("Spikes (1=pick, 2=pickaxe)##wpn-pick-count", ref p.PickSpikeCount, 1, 2);
                changed |= ImGui.SliderFloat("Head width##wpn-pick-w", ref p.PickHeadWidth, 0.5f, 4f, "%.1f");
                if (p.PickSpikeCount == 1)
                {
                    ImGui.SetNextItemWidth(150f);
                    changed |= ImGui.Combo("Back##wpn-pick-back", ref p.PickBack, WeaponPickBackLabels, WeaponPickBackLabels.Length);
                    if ((WeaponPickBack)p.PickBack != WeaponPickBack.None)
                        changed |= ImGui.SliderFloat("Back length##wpn-pick-backlen", ref p.PickBackLength, 0.5f, 10f, "%.1f");
                }
                break;

            case WeaponHeadType.Spade:
                changed |= ImGui.SliderFloat("Length##wpn-spade-l", ref p.SpadeLength, 1f, 12f, "%.1f");
                changed |= ImGui.SliderFloat("Width##wpn-spade-w", ref p.SpadeWidth, 1f, 12f, "%.1f");
                changed |= ImGui.SliderFloat("Thickness##wpn-spade-t", ref p.SpadeThickness, 0.2f, 2f, "%.2f");
                changed |= ImGui.Checkbox("Pointed (spade)##wpn-spade-point", ref p.SpadePointed);
                ImGui.SameLine(); changed |= ImGui.Checkbox("Foot step##wpn-spade-step", ref p.SpadeStep);
                break;

            case WeaponHeadType.Hoe:
                changed |= ImGui.SliderFloat("Blade width##wpn-hoe-w", ref p.HoeBladeWidth, 1f, 10f, "%.1f");
                changed |= ImGui.SliderFloat("Blade length##wpn-hoe-l", ref p.HoeBladeLength, 1f, 8f, "%.1f");
                changed |= ImGui.SliderFloat("Blade drop##wpn-hoe-drop", ref p.HoeBladeDrop, 0f, 110f, "%.0f deg");
                changed |= ImGui.SliderInt("Prongs (1=hoe, 3=cultivator)##wpn-hoe-prongs", ref p.HoeProngs, 1, 4);
                if (p.HoeProngs > 1) changed |= ImGui.SliderFloat("Prong spread##wpn-hoe-spread", ref p.HoeProngSpread, 0.5f, 5f, "%.1f");
                break;

            case WeaponHeadType.Scythe:
                changed |= ImGui.SliderInt("Segments##wpn-scy-seg", ref p.ScytheSegments, 2, 10);
                changed |= ImGui.SliderFloat("Length##wpn-scy-len", ref p.ScytheLength, 3f, 32f, "%.1f");
                changed |= ImGui.SliderFloat("Width##wpn-scy-w", ref p.ScytheWidth, 0.5f, 6f, "%.1f");
                changed |= ImGui.SliderFloat("Curve##wpn-scy-curve", ref p.ScytheCurve, 0f, 30f, "%.1f");
                changed |= ImGui.SliderFloat("Mount angle##wpn-scy-mount", ref p.ScytheMount, 0f, 110f, "%.0f deg");
                break;

            case WeaponHeadType.Bow:
                changed |= ImGui.Combo("Bow style##wpn-bow-style", ref p.BowStyle, WeaponBowStyleLabels, WeaponBowStyleLabels.Length);
                changed |= ImGui.SliderInt("Limb segments##wpn-bow-seg", ref p.BowLimbSegments, 2, 8);
                changed |= ImGui.SliderFloat("Limb length##wpn-bow-len", ref p.BowLimbLength, 4f, 24f, "%.1f");
                changed |= ImGui.SliderFloat("Limb width##wpn-bow-w", ref p.BowLimbWidth, 0.4f, 4f, "%.1f");
                changed |= ImGui.SliderFloat("Limb thickness##wpn-bow-t", ref p.BowLimbThickness, 0.3f, 3f, "%.1f");
                changed |= ImGui.SliderFloat("Recurve##wpn-bow-recurve", ref p.BowRecurve, 0f, 25f, "%.1f");
                changed |= ImGui.SliderFloat("Limb taper##wpn-bow-taper", ref p.BowLimbTaper, 0.1f, 1.1f, "%.2f");
                changed |= ImGui.SliderFloat("Riser length##wpn-bow-riser", ref p.BowRiserLength, 1f, 10f, "%.1f");
                changed |= ImGui.Checkbox("String##wpn-bow-string", ref p.BowString);
                ImGui.SameLine(); changed |= ImGui.Checkbox("Grip wrap##wpn-bow-grip", ref p.BowGripWrap);
                ImGui.SameLine(); changed |= ImGui.Checkbox("Arrow rest##wpn-bow-rest", ref p.BowArrowRest);
                break;

            case WeaponHeadType.Staff:
                changed |= ImGui.Checkbox("End knobs##wpn-staff-knobs", ref p.StaffEndKnobs);
                if (p.StaffEndKnobs) changed |= ImGui.SliderFloat("Knob size##wpn-staff-knobsize", ref p.StaffKnobSize, 0.5f, 5f, "%.1f");
                changed |= ImGui.Checkbox("Club head##wpn-staff-club", ref p.ClubHead);
                if (p.ClubHead)
                {
                    changed |= ImGui.SliderFloat("Club size##wpn-staff-clubsize", ref p.ClubHeadSize, 1f, 7f, "%.1f");
                    changed |= ImGui.SliderFloat("Club length##wpn-staff-clublen", ref p.ClubHeadLength, 1f, 10f, "%.1f");
                    changed |= ImGui.SliderInt("Studs##wpn-staff-studs", ref p.ClubStuds, 0, 12);
                }
                break;
        }
        return changed;
    }

    // ---- Archetype presets -------------------------------------------------

    private void ModelApplyWeaponArchetype(WeaponArchetype archetype)
    {
        WeaponParams p = _weaponParams;
        ModelResetWeaponGeometry(p);

        void Name(string name, string code) { p.Name = name; p.Code = code; }
        void Blade(int seg, float len, float bw, float tw, float th, float curve, WeaponBladeTip tip)
        {
            p.HeadType = (int)WeaponHeadType.Blade;
            p.BladeSegments = seg; p.BladeLength = len; p.BladeBaseWidth = bw; p.BladeTipWidth = tw;
            p.BladeThickness = th; p.BladeTipThickness = th * 0.6f; p.BladeCurve = curve; p.BladeTip = (int)tip;
        }
        void Item(string tool, string dmg, float power, float range, int dura, int tier)
        {
            p.ToolType = tool; p.DamageType = dmg; p.AttackPower = power; p.AttackRange = range; p.Durability = dura; p.ToolTier = tier;
        }

        switch (archetype)
        {
            case WeaponArchetype.Sword:
                Name("Custom Sword", "customsword");
                p.GripLength = 5f; p.GuardStyle = (int)WeaponGuardStyle.Crossguard; p.GuardWidth = 6f;
                p.PommelStyle = (int)WeaponPommelStyle.Ball;
                Blade(4, 22f, 2.6f, 0.5f, 0.45f, 0f, WeaponBladeTip.Point); p.BladeFuller = true;
                p.BladeSection = (int)WeaponBladeSection.DoubleBevel; p.GripSection = (int)WeaponGripSection.Oval; p.PommelPeen = true;
                Item("sword", "SlashingAttack", 5.5f, 1.5f, 1100, 4);
                break;
            case WeaponArchetype.Greatsword:
                Name("Greatsword", "greatsword");
                p.GripLength = 9f; p.GripWidth = 1.6f; p.GuardStyle = (int)WeaponGuardStyle.Crossguard; p.GuardWidth = 9f;
                p.PommelStyle = (int)WeaponPommelStyle.Wheel; p.PommelSize = 2.2f;
                Blade(6, 36f, 3.4f, 0.6f, 0.55f, 0f, WeaponBladeTip.Point); p.BladeFuller = true; p.Ricasso = true;
                p.BladeSection = (int)WeaponBladeSection.DoubleBevel; p.BladeSpine = (int)WeaponBladeSpine.FalseEdge; p.WrapRings = 4;
                Item("sword", "SlashingAttack", 9f, 2.6f, 1800, 5);
                break;
            case WeaponArchetype.Dagger:
                Name("Dagger", "dagger");
                p.GripLength = 3.5f; p.GripWidth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.Crossguard; p.GuardWidth = 3.5f;
                p.PommelStyle = (int)WeaponPommelStyle.Ball; p.PommelSize = 1.3f;
                Blade(2, 9f, 1.8f, 0.4f, 0.4f, 0f, WeaponBladeTip.Point); p.BladeDoubleEdge = true; p.BladeFuller = true;
                p.BladeSection = (int)WeaponBladeSection.Diamond;
                Item("knife", "PiercingAttack", 3.5f, 1.2f, 500, 3);
                break;
            case WeaponArchetype.Knife:
                Name("Knife", "knife");
                p.GripLength = 3.2f; p.GripWidth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap; p.PommelSize = 1.1f;
                Blade(2, 7f, 1.6f, 0.4f, 0.35f, 2f, WeaponBladeTip.Clip);
                Item("knife", "SlashingAttack", 2.5f, 1f, 400, 3);
                break;
            case WeaponArchetype.Rapier:
                Name("Rapier", "rapier");
                p.GripLength = 5.5f; p.GuardStyle = (int)WeaponGuardStyle.Basket; p.GuardWidth = 5f; p.BasketBars = 4;
                p.PommelStyle = (int)WeaponPommelStyle.Ball;
                Blade(4, 30f, 1.2f, 0.3f, 0.45f, 0f, WeaponBladeTip.Spear); p.BladeMidrib = true;
                p.BladeSection = (int)WeaponBladeSection.Diamond; p.GuardCup = true;
                Item("sword", "PiercingAttack", 5f, 2.2f, 900, 4);
                break;
            case WeaponArchetype.Sabre:
                Name("Sabre", "sabre");
                p.GripLength = 5f; p.GuardStyle = (int)WeaponGuardStyle.Knuckle; p.GuardWidth = 4.5f;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                Blade(6, 24f, 2.4f, 0.5f, 0.4f, 5f, WeaponBladeTip.Clip);
                Item("sword", "SlashingAttack", 5.5f, 1.7f, 1000, 4);
                break;
            case WeaponArchetype.Katana:
                Name("Katana", "katana");
                p.GripLength = 7f; p.GripWidth = 1.3f; p.WrapRings = 6; p.WrapSize = 0.3f;
                p.GuardStyle = (int)WeaponGuardStyle.Disc; p.GuardWidth = 3.2f; p.GuardThickness = 0.6f;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                Blade(7, 28f, 2.2f, 0.6f, 0.45f, 3.2f, WeaponBladeTip.Tanto);
                p.BladeSection = (int)WeaponBladeSection.SingleBevel; p.BladeSpine = (int)WeaponBladeSpine.FalseEdge; p.BladeSpineFraction = 0.7f; p.GripSection = (int)WeaponGripSection.Ribbed;
                Item("sword", "SlashingAttack", 6.5f, 1.9f, 1300, 5);
                break;
            case WeaponArchetype.Khopesh:
                Name("Khopesh", "khopesh");
                p.GripLength = 5f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Faceted;
                Blade(7, 22f, 2f, 0.8f, 0.5f, 9f, WeaponBladeTip.Spatula);
                Item("sword", "SlashingAttack", 5.5f, 1.6f, 900, 4);
                break;
            case WeaponArchetype.Gladius:
                Name("Gladius", "gladius");
                p.GripLength = 4.5f; p.GuardStyle = (int)WeaponGuardStyle.Crossguard; p.GuardWidth = 4f;
                p.PommelStyle = (int)WeaponPommelStyle.Ball; p.PommelSize = 2.2f;
                Blade(3, 18f, 2.8f, 1.2f, 0.5f, 0f, WeaponBladeTip.Spear);
                p.BladeSection = (int)WeaponBladeSection.DoubleBevel; p.BladeFuller = true;
                Item("sword", "StabbingAttack", 5f, 1.4f, 1000, 4);
                break;
            case WeaponArchetype.Falchion:
                Name("Falchion", "falchion");
                p.GripLength = 5f; p.GuardStyle = (int)WeaponGuardStyle.Crossguard; p.GuardWidth = 5f;
                p.PommelStyle = (int)WeaponPommelStyle.Ball;
                Blade(4, 20f, 3.2f, 2f, 0.55f, 3f, WeaponBladeTip.Clip);
                Item("sword", "SlashingAttack", 6f, 1.5f, 950, 4);
                break;
            case WeaponArchetype.Machete:
                Name("Machete", "machete");
                p.GripLength = 4.5f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                Blade(3, 18f, 2.4f, 1.6f, 0.35f, 2f, WeaponBladeTip.Spatula);
                Item("sword", "SlashingAttack", 4f, 1.4f, 600, 3);
                break;
            case WeaponArchetype.Scimitar:
                Name("Scimitar", "scimitar");
                p.GripLength = 5f; p.GuardStyle = (int)WeaponGuardStyle.Crossguard; p.GuardWidth = 5f;
                p.PommelStyle = (int)WeaponPommelStyle.Ball;
                Blade(7, 24f, 2.6f, 0.6f, 0.45f, 6f, WeaponBladeTip.Clip);
                Item("sword", "SlashingAttack", 5.5f, 1.7f, 950, 4);
                break;

            case WeaponArchetype.Spear:
                Name("Spear", "spear");
                p.GripLength = 34f; p.GripWidth = 1.3f; p.GripDepth = 1.3f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap; p.Langets = 2;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearHead = (int)WeaponSpearHead.Leaf; p.SpearHeadLength = 7f;
                Item("spear", "PiercingAttack", 5.5f, 3.5f, 800, 4);
                break;
            case WeaponArchetype.Pike:
                Name("Pike", "pike");
                p.GripLength = 46f; p.GripWidth = 1.2f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearHead = (int)WeaponSpearHead.Diamond; p.SpearHeadLength = 6f; p.SpearHeadWidth = 1.8f;
                Item("spear", "PiercingAttack", 6f, 4.5f, 700, 4);
                break;
            case WeaponArchetype.Javelin:
                Name("Javelin", "javelin");
                p.GripLength = 26f; p.GripWidth = 1f; p.GripDepth = 1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearHead = (int)WeaponSpearHead.Barbed; p.SpearHeadLength = 5f; p.SpearHeadWidth = 1.6f;
                Item("spear", "PiercingAttack", 4.5f, 3f, 200, 3);
                break;
            case WeaponArchetype.Trident:
                Name("Trident", "trident");
                p.GripLength = 32f; p.GripWidth = 1.3f; p.GripDepth = 1.3f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearHead = (int)WeaponSpearHead.Needle; p.SpearHeadLength = 6f;
                p.SpearProngs = 3; p.SpearProngSpread = 2.4f;
                Item("spear", "PiercingAttack", 6f, 3.4f, 900, 4);
                break;
            case WeaponArchetype.Naginata:
                Name("Naginata", "naginata");
                p.GripLength = 34f; p.GripWidth = 1.3f; p.GripDepth = 1.3f; p.GuardStyle = (int)WeaponGuardStyle.Disc; p.GuardWidth = 2.6f; p.GuardThickness = 0.6f;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearCurvedBlade = true; p.SpearHeadLength = 9f; p.SpearHeadWidth = 2.2f; p.BladeCurve = 7f;
                Item("spear", "SlashingAttack", 6.5f, 3.4f, 1000, 4);
                break;
            case WeaponArchetype.Glaive:
                Name("Glaive", "glaive");
                p.GripLength = 36f; p.GripWidth = 1.4f; p.GripDepth = 1.4f; p.GuardStyle = (int)WeaponGuardStyle.None; p.Langets = 2;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearCurvedBlade = true; p.SpearHeadLength = 11f; p.SpearHeadWidth = 2.6f; p.BladeCurve = 5f;
                Item("spear", "SlashingAttack", 7f, 3.6f, 1100, 5);
                break;
            case WeaponArchetype.Halberd:
                Name("Halberd", "halberd");
                p.GripLength = 40f; p.GripWidth = 1.4f; p.GripDepth = 1.4f; p.GuardStyle = (int)WeaponGuardStyle.None; p.Langets = 2;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearHead = (int)WeaponSpearHead.Diamond; p.SpearHeadLength = 6f; p.SpearHeadWidth = 1.8f;
                p.SpearSideAxe = true; p.SpearBackHook = true;
                Item("spear", "SlashingAttack", 7.5f, 3.5f, 1200, 5);
                break;
            case WeaponArchetype.Partisan:
                Name("Partisan", "partisan");
                p.GripLength = 38f; p.GripWidth = 1.4f; p.GripDepth = 1.4f; p.GuardStyle = (int)WeaponGuardStyle.None; p.Langets = 2;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Spear; p.SpearHead = (int)WeaponSpearHead.Leaf; p.SpearHeadLength = 8f; p.SpearWings = true; p.SpearWingLength = 3.5f;
                Item("spear", "PiercingAttack", 6.5f, 3.5f, 1100, 4);
                break;

            case WeaponArchetype.Axe:
                Name("Axe", "axe");
                p.GripLength = 16f; p.GripWidth = 1.4f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Axe; p.AxeBitWidth = 6f; p.AxeBitDepth = 4f; p.AxeBeard = 1.5f; p.AxeHeadPos = 0.95f;
                Item("axe", "SlashingAttack", 4.5f, 1.5f, 900, 4); p.MiningSpeed = 6f;
                break;
            case WeaponArchetype.Battleaxe:
                Name("Battle Axe", "battleaxe");
                p.GripLength = 24f; p.GripWidth = 1.5f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Axe; p.AxeBitWidth = 9f; p.AxeBitDepth = 6f; p.AxeBeard = 3f; p.AxeSweep = 16f; p.AxeHeadPos = 0.92f; p.AxeBack = (int)WeaponAxeBack.Spike;
                Item("axe", "SlashingAttack", 7.5f, 1.7f, 1300, 5);
                break;
            case WeaponArchetype.Bardiche:
                Name("Bardiche", "bardiche");
                p.GripLength = 30f; p.GripWidth = 1.4f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Axe; p.AxeBitWidth = 14f; p.AxeBitDepth = 4f; p.AxeBeard = 5f; p.AxeSweep = 22f; p.AxeHeadPos = 0.88f;
                Item("axe", "SlashingAttack", 8f, 2.2f, 1200, 5);
                break;
            case WeaponArchetype.BeardedAxe:
                Name("Bearded Axe", "beardedaxe");
                p.GripLength = 18f; p.GripWidth = 1.4f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Axe; p.AxeBitWidth = 8f; p.AxeBitDepth = 4f; p.AxeBeard = 5f; p.AxeHeadPos = 0.94f;
                Item("axe", "SlashingAttack", 5.5f, 1.6f, 950, 4); p.MiningSpeed = 6f;
                break;
            case WeaponArchetype.DoubleAxe:
                Name("Double Axe", "doubleaxe");
                p.GripLength = 22f; p.GripWidth = 1.5f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Axe; p.AxeBitWidth = 9f; p.AxeBitDepth = 5f; p.AxeBeard = 3f; p.AxeDoubleBit = true; p.AxeHeadPos = 0.92f;
                Item("axe", "SlashingAttack", 7f, 1.7f, 1200, 5);
                break;
            case WeaponArchetype.Hatchet:
                Name("Hatchet", "hatchet");
                p.GripLength = 11f; p.GripWidth = 1.3f; p.GripDepth = 1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Axe; p.AxeBitWidth = 5f; p.AxeBitDepth = 3.5f; p.AxeBeard = 1f; p.AxeHeadPos = 0.95f;
                Item("axe", "SlashingAttack", 3.5f, 1.3f, 600, 3); p.MiningSpeed = 5f;
                break;

            case WeaponArchetype.Warhammer:
                Name("War Hammer", "warhammer");
                p.GripLength = 22f; p.GripWidth = 1.5f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Hammer; p.HammerHeadLength = 3.5f; p.HammerHeadWidth = 3f; p.HammerHeadHeight = 3f; p.HammerBack = (int)WeaponHammerBack.Spike;
                Item("hammer", "BluntAttack", 7f, 1.7f, 1400, 5);
                break;
            case WeaponArchetype.Maul:
                Name("Maul", "maul");
                p.GripLength = 26f; p.GripWidth = 1.7f; p.GripDepth = 1.4f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Hammer; p.HammerHeadLength = 5f; p.HammerHeadWidth = 5f; p.HammerHeadHeight = 5f; p.HammerReinforced = true;
                Item("hammer", "BluntAttack", 9f, 1.8f, 1600, 5);
                break;
            case WeaponArchetype.Mace:
                Name("Mace", "mace");
                p.GripLength = 12f; p.GripWidth = 1.4f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Mace; p.MaceHead = (int)WeaponMaceHead.Ball; p.MaceHeadSize = 3.2f; p.FlangeCount = 6; p.FlangeType = (int)WeaponFlangeType.Flange;
                Item("mace", "BluntAttack", 6f, 1.5f, 1200, 4);
                break;
            case WeaponArchetype.Morningstar:
                Name("Morning Star", "morningstar");
                p.GripLength = 14f; p.GripWidth = 1.4f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Mace; p.MaceHead = (int)WeaponMaceHead.Ball; p.MaceHeadSize = 3.4f; p.FlangeCount = 8; p.FlangeType = (int)WeaponFlangeType.Spike; p.FlangeLength = 2.4f; p.FlangeRows = 2; p.MaceTopSpike = true;
                Item("mace", "PiercingAttack", 7f, 1.5f, 1100, 4);
                break;
            case WeaponArchetype.FlangedMace:
                Name("Flanged Mace", "flangedmace");
                p.GripLength = 13f; p.GripWidth = 1.4f; p.GripDepth = 1.2f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Mace; p.MaceHead = (int)WeaponMaceHead.Cylinder; p.MaceHeadSize = 3f; p.FlangeCount = 6; p.FlangeType = (int)WeaponFlangeType.Flange; p.FlangeLength = 2f;
                Item("mace", "BluntAttack", 6.5f, 1.5f, 1200, 4);
                break;
            case WeaponArchetype.Club:
                Name("Club", "club");
                p.GripLength = 11f; p.GripWidth = 1.3f; p.GripDepth = 1.3f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Staff; p.ClubHead = true; p.ClubHeadSize = 3f; p.ClubHeadLength = 5f;
                Item("club", "BluntAttack", 4f, 1.4f, 500, 2);
                break;
            case WeaponArchetype.Quarterstaff:
                Name("Quarterstaff", "quarterstaff");
                p.GripLength = 40f; p.GripWidth = 1.3f; p.GripDepth = 1.3f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Staff; p.StaffEndKnobs = true; p.StaffKnobSize = 1.8f;
                Item("staff", "BluntAttack", 4f, 2.2f, 700, 2);
                break;

            case WeaponArchetype.Pickaxe:
                Name("Pickaxe", "pickaxe");
                p.GripLength = 18f; p.GripWidth = 1.4f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Pick; p.PickSpikeCount = 2; p.PickSpikeLength = 7f; p.PickSpikeCurve = 14f;
                Item("pickaxe", "PiercingAttack", 3.5f, 1.4f, 900, 4); p.MiningSpeed = 8f;
                break;
            case WeaponArchetype.WarPick:
                Name("War Pick", "warpick");
                p.GripLength = 14f; p.GripWidth = 1.4f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Pick; p.PickSpikeCount = 1; p.PickSpikeLength = 7f; p.PickSpikeCurve = 20f; p.PickBack = (int)WeaponPickBack.Hammer;
                Item("pickaxe", "PiercingAttack", 6f, 1.5f, 1100, 4);
                break;
            case WeaponArchetype.Mattock:
                Name("Mattock", "mattock");
                p.GripLength = 18f; p.GripWidth = 1.4f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Pick; p.PickSpikeCount = 1; p.PickSpikeLength = 6f; p.PickSpikeCurve = 12f; p.PickBack = (int)WeaponPickBack.Adze;
                Item("pickaxe", "BluntAttack", 4f, 1.5f, 800, 3); p.MiningSpeed = 6f;
                break;
            case WeaponArchetype.Shovel:
                Name("Shovel", "shovel");
                p.GripLength = 20f; p.GripWidth = 1.4f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Spade; p.SpadeLength = 6f; p.SpadeWidth = 6f; p.SpadeStep = true;
                Item("shovel", "BluntAttack", 2.5f, 1.4f, 700, 3); p.MiningSpeed = 7f;
                break;
            case WeaponArchetype.Hoe:
                Name("Hoe", "hoe");
                p.GripLength = 22f; p.GripWidth = 1.3f; p.GripDepth = 1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Hoe; p.HoeBladeWidth = 5f; p.HoeBladeLength = 3.5f; p.HoeBladeDrop = 70f;
                Item("hoe", "BluntAttack", 2f, 1.4f, 500, 2);
                break;
            case WeaponArchetype.Scythe:
                Name("Scythe", "scythe");
                p.GripLength = 30f; p.GripWidth = 1.3f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Scythe; p.ScytheLength = 16f; p.ScytheCurve = 14f; p.ScytheMount = 80f;
                Item("scythe", "SlashingAttack", 4f, 2.4f, 700, 3);
                break;
            case WeaponArchetype.Sickle:
                Name("Sickle", "sickle");
                p.GripLength = 5f; p.GripWidth = 1.3f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.Cap;
                p.HeadType = (int)WeaponHeadType.Scythe; p.ScytheLength = 9f; p.ScytheCurve = 22f; p.ScytheMount = 40f; p.ScytheWidth = 1.6f;
                Item("sickle", "SlashingAttack", 2.5f, 1.2f, 400, 2);
                break;
            case WeaponArchetype.SmithHammer:
                Name("Hammer", "smithhammer");
                p.GripLength = 9f; p.GripWidth = 1.3f; p.GripDepth = 1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Hammer; p.HammerHeadLength = 3.5f; p.HammerHeadWidth = 2.6f; p.HammerHeadHeight = 2.6f; p.HammerBack = (int)WeaponHammerBack.CrossPeen;
                Item("hammer", "BluntAttack", 3f, 1.2f, 600, 3); p.MiningSpeed = 4f;
                break;
            case WeaponArchetype.Saw:
                Name("Saw", "saw");
                p.GripLength = 5f; p.GripWidth = 1.4f; p.GripDepth = 1.1f; p.GuardStyle = (int)WeaponGuardStyle.None;
                p.PommelStyle = (int)WeaponPommelStyle.None;
                Blade(2, 14f, 3f, 2.6f, 0.25f, 0f, WeaponBladeTip.Spatula); p.BladeSerrations = 12;
                Item("saw", "SlashingAttack", 2f, 1.3f, 600, 3);
                break;

            case WeaponArchetype.Shortbow:
                Name("Shortbow", "shortbow");
                p.GripLength = 2f; p.GuardStyle = (int)WeaponGuardStyle.None; p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Bow; p.BowStyle = (int)WeaponBowStyle.Straight; p.BowLimbLength = 11f; p.BowRecurve = 6f;
                Item("bow", "PiercingAttack", 1.5f, 1f, 400, 3);
                break;
            case WeaponArchetype.Longbow:
                Name("Longbow", "longbow");
                p.GripLength = 2f; p.GuardStyle = (int)WeaponGuardStyle.None; p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Bow; p.BowStyle = (int)WeaponBowStyle.Longbow; p.BowLimbLength = 18f; p.BowRecurve = 6f; p.BowLimbSegments = 5;
                Item("bow", "PiercingAttack", 2f, 1f, 600, 4);
                break;
            case WeaponArchetype.Recurvebow:
                Name("Recurve Bow", "recurvebow");
                p.GripLength = 2f; p.GuardStyle = (int)WeaponGuardStyle.None; p.PommelStyle = (int)WeaponPommelStyle.None;
                p.HeadType = (int)WeaponHeadType.Bow; p.BowStyle = (int)WeaponBowStyle.Recurve; p.BowLimbLength = 13f; p.BowRecurve = 14f;
                Item("bow", "PiercingAttack", 1.8f, 1f, 550, 3);
                break;
        }
    }

    /// <summary>Resets every geometry / head / item field to the WeaponParams defaults, keeping only identity,
    /// placement, textures and seed so a preset starts from a clean slate.</summary>
    private static void ModelResetWeaponGeometry(WeaponParams p)
    {
        WeaponParams d = new();
        p.GripLength = d.GripLength; p.GripWidth = d.GripWidth; p.GripDepth = d.GripDepth; p.GripTaper = d.GripTaper;
        p.GripSection = d.GripSection; p.GripSwell = d.GripSwell; p.GripCollar = d.GripCollar;
        p.WrapRings = d.WrapRings; p.WrapSize = d.WrapSize; p.Langets = d.Langets; p.LangetLength = d.LangetLength;
        p.GuardStyle = d.GuardStyle; p.GuardWidth = d.GuardWidth; p.GuardDepth = d.GuardDepth; p.GuardThickness = d.GuardThickness;
        p.GuardForward = d.GuardForward; p.GuardCup = d.GuardCup; p.QuillonDroop = d.QuillonDroop; p.BasketBars = d.BasketBars;
        p.PommelStyle = d.PommelStyle; p.PommelSize = d.PommelSize; p.PommelLength = d.PommelLength; p.PommelPeen = d.PommelPeen;
        p.HeadType = d.HeadType;
        p.BladeSegments = d.BladeSegments; p.BladeLength = d.BladeLength; p.BladeBaseWidth = d.BladeBaseWidth; p.BladeTipWidth = d.BladeTipWidth;
        p.BladeThickness = d.BladeThickness; p.BladeTipThickness = d.BladeTipThickness; p.BladeCurve = d.BladeCurve; p.BladeTip = d.BladeTip; p.BladeTipLength = d.BladeTipLength;
        p.BladeDoubleEdge = d.BladeDoubleEdge; p.BladeFuller = d.BladeFuller; p.BladeFullerWidth = d.BladeFullerWidth; p.BladeMidrib = d.BladeMidrib; p.BladeSerrations = d.BladeSerrations; p.Ricasso = d.Ricasso;
        p.BladeSection = d.BladeSection; p.BladeBevels = d.BladeBevels; p.BladeBevelWidth = d.BladeBevelWidth; p.BladeBevelAngle = d.BladeBevelAngle;
        p.BladeEdgeThickness = d.BladeEdgeThickness; p.BladeSpine = d.BladeSpine; p.BladeSpineFraction = d.BladeSpineFraction;
        p.AxeBitWidth = d.AxeBitWidth; p.AxeBitDepth = d.AxeBitDepth; p.AxeBitThickness = d.AxeBitThickness; p.AxeBeard = d.AxeBeard; p.AxeTopHorn = d.AxeTopHorn;
        p.AxeSweep = d.AxeSweep; p.AxeEdgeProfile = d.AxeEdgeProfile; p.AxeCheeks = d.AxeCheeks; p.AxeDoubleBit = d.AxeDoubleBit; p.AxeBack = d.AxeBack; p.AxeBackLength = d.AxeBackLength; p.AxeHeadPos = d.AxeHeadPos; p.AxeEye = d.AxeEye;
        p.HammerHeadLength = d.HammerHeadLength; p.HammerHeadWidth = d.HammerHeadWidth; p.HammerHeadHeight = d.HammerHeadHeight; p.HammerFaceBevel = d.HammerFaceBevel; p.HammerFaceShape = d.HammerFaceShape;
        p.HammerBack = d.HammerBack; p.HammerBackLength = d.HammerBackLength; p.HammerReinforced = d.HammerReinforced; p.HammerCheeks = d.HammerCheeks;
        p.MaceHead = d.MaceHead; p.MaceHeadSize = d.MaceHeadSize; p.FlangeCount = d.FlangeCount; p.FlangeType = d.FlangeType;
        p.FlangeLength = d.FlangeLength; p.FlangeWidth = d.FlangeWidth; p.FlangeRows = d.FlangeRows; p.FlangeTwist = d.FlangeTwist; p.MaceNeck = d.MaceNeck; p.MaceTopSpike = d.MaceTopSpike; p.MaceTopSpikeLength = d.MaceTopSpikeLength;
        p.SocketLength = d.SocketLength; p.SpearHeadLength = d.SpearHeadLength; p.SpearHeadWidth = d.SpearHeadWidth; p.SpearHeadThickness = d.SpearHeadThickness;
        p.SpearHead = d.SpearHead; p.SpearMidrib = d.SpearMidrib; p.SpearWings = d.SpearWings; p.SpearWingLength = d.SpearWingLength;
        p.SpearProngs = d.SpearProngs; p.SpearProngSpread = d.SpearProngSpread; p.SpearFuller = d.SpearFuller; p.SocketRings = d.SocketRings;
        p.SpearBackHook = d.SpearBackHook; p.SpearBackHookLength = d.SpearBackHookLength;
        p.SpearSideAxe = d.SpearSideAxe; p.SpearCurvedBlade = d.SpearCurvedBlade;
        p.PickSpikeLength = d.PickSpikeLength; p.PickSpikeCurve = d.PickSpikeCurve; p.PickSpikeCount = d.PickSpikeCount; p.PickHeadWidth = d.PickHeadWidth;
        p.PickBack = d.PickBack; p.PickBackLength = d.PickBackLength;
        p.SpadeLength = d.SpadeLength; p.SpadeWidth = d.SpadeWidth; p.SpadeThickness = d.SpadeThickness; p.SpadeCurl = d.SpadeCurl; p.SpadePointed = d.SpadePointed; p.SpadeStep = d.SpadeStep;
        p.HoeBladeWidth = d.HoeBladeWidth; p.HoeBladeLength = d.HoeBladeLength; p.HoeBladeDrop = d.HoeBladeDrop; p.HoeProngs = d.HoeProngs; p.HoeProngSpread = d.HoeProngSpread;
        p.ScytheSegments = d.ScytheSegments; p.ScytheLength = d.ScytheLength; p.ScytheWidth = d.ScytheWidth; p.ScytheCurve = d.ScytheCurve; p.ScytheMount = d.ScytheMount; p.ScytheThickness = d.ScytheThickness;
        p.BowStyle = d.BowStyle; p.BowLimbSegments = d.BowLimbSegments; p.BowLimbLength = d.BowLimbLength; p.BowLimbThickness = d.BowLimbThickness;
        p.BowLimbWidth = d.BowLimbWidth; p.BowRecurve = d.BowRecurve; p.BowLimbTaper = d.BowLimbTaper; p.BowRiserLength = d.BowRiserLength; p.BowString = d.BowString; p.BowGripWrap = d.BowGripWrap; p.BowArrowRest = d.BowArrowRest;
        p.StaffEndKnobs = d.StaffEndKnobs; p.StaffKnobSize = d.StaffKnobSize; p.ClubHead = d.ClubHead; p.ClubHeadSize = d.ClubHeadSize; p.ClubHeadLength = d.ClubHeadLength; p.ClubStuds = d.ClubStuds;
        p.ToolType = d.ToolType; p.AttackPower = d.AttackPower; p.AttackRange = d.AttackRange; p.Durability = d.Durability; p.ToolTier = d.ToolTier; p.MiningSpeed = d.MiningSpeed; p.DamageType = d.DamageType;
    }

    // ---- Randomize ---------------------------------------------------------

    private void ModelRandomizeWeapon(WeaponParams p)
    {
        Random r = new(p.Seed);
        float Range(float min, float max) => min + (float)r.NextDouble() * (max - min);

        // Start from a random archetype so the family makes sense, then jitter within it.
        _weaponArchetypeIndex = r.Next(0, WeaponArchetypeLabels.Length);
        ModelApplyWeaponArchetype((WeaponArchetype)_weaponArchetypeIndex);

        p.GripLength = Math.Max(2f, p.GripLength * Range(0.8f, 1.25f));
        p.GripWidth = Math.Clamp(p.GripWidth * Range(0.8f, 1.3f), 0.5f, 3f);

        switch ((WeaponHeadType)p.HeadType)
        {
            case WeaponHeadType.Blade:
                p.BladeLength = Math.Clamp(p.BladeLength * Range(0.7f, 1.35f), 4f, 44f);
                p.BladeBaseWidth = Math.Clamp(p.BladeBaseWidth * Range(0.7f, 1.4f), 0.6f, 6f);
                p.BladeThickness = Range(0.3f, 0.8f);
                p.BladeTipThickness = p.BladeThickness * Range(0.4f, 0.8f);
                p.BladeCurve = Range(-2f, 10f);
                p.BladeTip = r.Next(0, WeaponBladeTipLabels.Length);
                p.BladeSection = r.Next(0, WeaponBladeSectionLabels.Length);
                p.BladeBevelWidth = Range(0.3f, 0.7f);
                p.BladeBevelAngle = Range(12f, 32f);
                p.BladeSpine = r.NextDouble() < 0.4 ? (int)WeaponBladeSpine.FalseEdge : (int)WeaponBladeSpine.Plain;
                p.BladeFuller = r.NextDouble() < 0.6;
                p.BladeMidrib = r.NextDouble() < 0.3;
                break;
            case WeaponHeadType.Axe:
                p.AxeBitWidth = Range(4f, 13f); p.AxeBitDepth = Range(3f, 7f); p.AxeBeard = Range(0f, 5f);
                p.AxeSweep = Range(0f, 22f); p.AxeBack = r.Next(0, WeaponAxeBackLabels.Length); p.AxeDoubleBit = r.NextDouble() < 0.25;
                break;
            case WeaponHeadType.Hammer:
                p.HammerHeadLength = Range(2.5f, 6f); p.HammerHeadWidth = Range(2.5f, 6f); p.HammerHeadHeight = Range(2.5f, 6f);
                p.HammerBack = r.Next(0, WeaponHammerBackLabels.Length);
                break;
            case WeaponHeadType.Mace:
                p.MaceHead = r.Next(0, WeaponMaceHeadLabels.Length); p.MaceHeadSize = Range(2.5f, 4.5f);
                p.FlangeCount = r.Next(4, 10); p.FlangeType = r.Next(0, WeaponFlangeTypeLabels.Length); p.FlangeLength = Range(1f, 3f);
                p.FlangeRows = r.Next(1, 3); p.MaceTopSpike = r.NextDouble() < 0.5;
                break;
            case WeaponHeadType.Spear:
                p.SpearHead = r.Next(0, WeaponSpearHeadLabels.Length); p.SpearHeadLength = Range(4f, 11f);
                p.SpearProngs = r.NextDouble() < 0.3 ? 3 : 1; p.SpearWings = r.NextDouble() < 0.3;
                p.SpearBackHook = r.NextDouble() < 0.25; p.SpearSideAxe = r.NextDouble() < 0.2;
                break;
            case WeaponHeadType.Pick:
                p.PickSpikeLength = Range(5f, 10f); p.PickSpikeCurve = Range(8f, 24f); p.PickSpikeCount = r.Next(1, 3);
                p.PickBack = r.Next(0, WeaponPickBackLabels.Length);
                break;
            case WeaponHeadType.Scythe:
                p.ScytheLength = Range(8f, 22f); p.ScytheCurve = Range(8f, 24f); p.ScytheMount = Range(30f, 95f);
                break;
            case WeaponHeadType.Bow:
                p.BowStyle = r.Next(0, WeaponBowStyleLabels.Length); p.BowLimbLength = Range(9f, 20f); p.BowRecurve = Range(2f, 18f);
                break;
        }

        p.PommelStyle = r.Next(0, WeaponPommelStyleLabels.Length);
        p.WrapRings = r.NextDouble() < 0.4 ? r.Next(3, 8) : 0;
    }

    // ---- Item JSON ---------------------------------------------------------

    private JObject ModelWeaponBuildItemJson()
    {
        WeaponParams p = _weaponParams;
        string code = ModelSanitizeFileName(p.Code);
        string metal = ModelWeaponResolveTexture(p.Texture, "metal");
        string metalPath = _modelDoc?.Textures.FirstOrDefault(texture => string.Equals(texture.Code, metal, StringComparison.Ordinal))?.Path
            ?? _modelDoc?.Textures.FirstOrDefault()?.Path
            ?? "block/metal/ingot/iron";

        JObject item = new()
        {
            ["code"] = code,
            ["class"] = "Item",
            ["maxstacksize"] = 1,
            ["damagedby"] = new JArray("blockbreaking", "attacking"),
            ["tool"] = p.ToolType,
            ["attackpower"] = Math.Round(p.AttackPower, 2),
            ["attackrange"] = Math.Round(p.AttackRange, 2),
            ["durability"] = Math.Max(1, p.Durability),
            ["tooltier"] = Math.Clamp(p.ToolTier, 0, 99),
            ["creativeinventory"] = new JObject { ["general"] = new JArray("*"), ["items"] = new JArray("*"), ["tools"] = new JArray("*") }
        };
        if (p.MiningSpeed > 0f)
        {
            item["miningmaterial"] = new JObject(); // placeholder; author fills miningSpeedByType
            item["miningspeed"] = new JObject { ["stone"] = Math.Round(p.MiningSpeed, 2) };
        }
        if (!string.IsNullOrWhiteSpace(p.DamageType))
        {
            item["attributes"] = new JObject { ["damageType"] = p.DamageType };
        }
        item["behaviors"] = new JArray(new JObject { ["name"] = "GroundStorable", ["properties"] = new JObject { ["layout"] = "WallHalves", ["sprintKey"] = true } });
        item["shape"] = new JObject { ["base"] = ModelWeaponShapePath(code) };
        item["textures"] = new JObject { [metal] = new JObject { ["base"] = ModelWeaponTexturePath(metalPath) } };
        item["guiTransform"] = new JObject
        {
            ["rotation"] = new JObject { ["x"] = -90, ["y"] = 45, ["z"] = 35 },
            ["origin"] = new JObject { ["x"] = 0.55, ["y"] = 0.0, ["z"] = 0.48 },
            ["scale"] = 1.25
        };
        item["fpHandTransform"] = new JObject
        {
            ["rotation"] = new JObject { ["x"] = 5, ["y"] = 10, ["z"] = 90 },
            ["origin"] = new JObject { ["x"] = 0.2, ["y"] = 0.5, ["z"] = 0.6 },
            ["scale"] = 4
        };
        item["tpHandTransform"] = new JObject
        {
            ["translation"] = new JObject { ["x"] = -1.25, ["y"] = -0.55, ["z"] = -1.0 },
            ["rotation"] = new JObject { ["x"] = -96, ["y"] = 0, ["z"] = 0 },
            ["scale"] = 0.95
        };
        item["groundTransform"] = new JObject
        {
            ["translation"] = new JObject { ["x"] = 0, ["y"] = 0.4, ["z"] = 0 },
            ["origin"] = new JObject { ["x"] = 0.5, ["y"] = 0.5, ["z"] = 0.53 },
            ["scale"] = 4
        };
        return item;
    }

    private string ModelWeaponShapePath(string code)
    {
        if (_modelDoc != null && !string.IsNullOrWhiteSpace(_modelDoc.AssetPath))
        {
            string path = _modelDoc.AssetPath.Replace('\\', '/');
            if (path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase)) path = path["shapes/".Length..];
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^".json".Length];
            return $"{_modelDoc.Domain}:{path}";
        }
        return $"game:item/tool/{code}";
    }

    private static string ModelWeaponTexturePath(string path)
    {
        string trimmed = path.Replace('\\', '/');
        if (trimmed.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed["textures/".Length..];
        return trimmed;
    }

    private void ModelWeaponExportItem()
    {
        try
        {
            string code = ModelSanitizeFileName(_weaponParams.Code);
            string domain = _modelDoc != null && !string.IsNullOrWhiteSpace(_modelDoc.Domain) ? _modelDoc.Domain : "game";
            string json = ModelWeaponBuildItemJson().ToString(Formatting.Indented);
            string relative = Path.Combine("assets", domain, "itemtypes", "tool", code + ".json");
            string outputPath = GetToolAuthoredAssetPath("weapons", relative);
            string writeError = WriteAuthoredFile(outputPath, json);
            _weaponStatus = string.IsNullOrEmpty(writeError)
                ? $"Exported item JSON to {outputPath}"
                : $"Item export failed: {writeError}";
        }
        catch (Exception exception)
        {
            _weaponStatus = $"Item export failed: {exception.Message}";
        }
    }
}
