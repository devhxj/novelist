# Reference Style Taxonomy v1

Version: `reference-style-taxonomy-v1`

This taxonomy defines the first supported advanced style labels for Phase 14. The code-level source of truth is `ReferenceStyleTaxonomy` in `src/Novelist.Contracts/App/ReferenceStylePayloads.cs`; this document is the reviewable human-readable map.

LLM-assisted analysis may emit only these `feature_key` values and only the listed labels for each key. Every accepted label must still carry confidence, analyzer source, and grounded evidence spans through `ReferenceStyleEvidenceSpanPayload`.

| Feature key | Category | Labels | Compatible beat duties |
| --- | --- | --- | --- |
| `narration_distance` | `narration` | `close_limited`, `mid_summary`, `distant_omniscient` | `interiority`, `pov_control`, `narration_distance` |
| `pov_control` | `narration` | `tight_internal`, `limited_external`, `head_hopping_risk` | `pov_control`, `viewpoint_boundary`, `interiority` |
| `rhythm` | `prose_rhythm` | `staccato`, `balanced`, `rolling_periodic` | `rhythm`, `pacing`, `pressure` |
| `sentence_shape` | `prose_rhythm` | `short_direct`, `layered_clause`, `fragment_pressure` | `sentence_shape`, `rhythm`, `anti_screenplay` |
| `paragraph_cadence` | `prose_rhythm` | `single_beat`, `braided_motion_reflection`, `long_wave` | `paragraph_cadence`, `rhythm`, `scene_flow` |
| `dialogue_mechanics` | `dialogue_and_subtext` | `short_turns`, `interrupted_exchange`, `subtext_reply` | `dialogue`, `subtext`, `external_evidence` |
| `subtext` | `dialogue_and_subtext` | `withheld_answer`, `displaced_topic`, `implication_gap` | `subtext`, `dialogue`, `emotion_pressure` |
| `externalized_emotion` | `dialogue_and_subtext` | `body_afterbeat`, `object_handling`, `silence_response` | `external_evidence`, `physical_afterbeat`, `interiority` |
| `sensory_image` | `imagery_and_sensation` | `tactile_grounding`, `auditory_pressure`, `visual_anchor` | `sensory_anchor`, `environment`, `source_backed_detail` |
| `metaphor_system` | `imagery_and_sensation` | `concrete_vehicle`, `recurring_symbol`, `abstract_risk` | `image_system`, `sensory_anchor`, `anti_ai_prose` |
| `image_system` | `imagery_and_sensation` | `weather_motif`, `object_motif`, `light_shadow_motif` | `image_system`, `sensory_anchor`, `theme_motif` |
| `tension_pressure` | `tension_and_structure` | `narrowing_options`, `ticking_clock`, `interpersonal_threat` | `pressure`, `conflict`, `escalation` |
| `hook_pattern` | `tension_and_structure` | `question_tail`, `reversal_tail`, `threat_arrival` | `hook`, `reader_question`, `pressure` |
| `payoff_pattern` | `tension_and_structure` | `answer_reveal`, `emotional_release`, `promise_fulfilled` | `payoff`, `reveal`, `emotion_turn` |
| `transition_pattern` | `tension_and_structure` | `time_jump`, `causal_bridge`, `scene_cut` | `transition`, `causality`, `scene_flow` |
| `exposition_handling` | `tension_and_structure` | `embedded_in_action`, `dialogue_exposition`, `infodump_risk` | `exposition`, `source_backed_detail`, `anti_ai_prose` |
| `action_clarity` | `tension_and_structure` | `clean_blocking`, `ambiguous_blocking`, `sequential_motion` | `action`, `blocking`, `external_evidence` |
| `anti_screenplay_prose` | `prose_rhythm` | `interiorized_action`, `camera_direction_risk`, `prose_afterbeat` | `anti_screenplay`, `interiority`, `physical_afterbeat` |
| `chapter_hook` | `web_fiction_mechanics` | `cliffhanger_question`, `new_threat`, `promise_open` | `hook`, `chapter_hook`, `reader_promise` |
| `escalation_beat` | `web_fiction_mechanics` | `complication`, `cost_increase`, `pressure_turn` | `escalation`, `pressure`, `conflict` |
| `payoff_beat` | `web_fiction_mechanics` | `reveal_payoff`, `emotional_payoff`, `tactical_payoff` | `payoff`, `reveal`, `pleasure_point` |
| `compression_expansion` | `web_fiction_mechanics` | `compressed_summary`, `expanded_moment`, `balanced_scene` | `pacing`, `rhythm`, `scene_focus` |
| `pleasure_point_delivery` | `web_fiction_mechanics` | `power_reversal`, `competence_display`, `emotional_catharsis` | `pleasure_point`, `payoff`, `reader_promise` |
| `cliffhanger_type` | `web_fiction_mechanics` | `danger_cut`, `secret_reveal`, `choice_suspended` | `hook`, `cliffhanger`, `reader_question` |
| `information_withholding` | `web_fiction_mechanics` | `fair_gap`, `delayed_identity`, `hidden_motive` | `reader_question`, `mystery`, `causality` |
| `reader_promise_tracking` | `web_fiction_mechanics` | `promise_planted`, `promise_renewed`, `promise_paid_off` | `reader_promise`, `hook`, `payoff` |

Validation rules:

- Unknown feature keys are rejected.
- Unknown labels for a supported feature key are rejected.
- Accepted labels must cite source segment ids and offsets inside supplied bounded windows.
- Accepted evidence carries `confidence`, `analyzer_source`, source ids, offsets, and `text_hash`, but not source text.
