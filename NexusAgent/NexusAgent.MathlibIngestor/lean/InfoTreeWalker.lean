import Lean

open Lean Elab Tactic Meta

namespace NexusMathlibExtractor

/--
  Phase-1 extractor skeleton.
  Walks TacticInfo nodes and emits transition rows with:
    module_source, theorem_source, goal_before, tactic_raw, goals_after

  This file is intentionally a scaffold for integration into a mathlib/lake
  build environment where InfoTree is available for every elaborated declaration.
-/

structure TransitionRow where
  moduleSource : String
  theoremSource : String
  goalBefore : String
  tacticRaw : String
  goalsAfter : List String
  deriving Inhabited, Repr

private def goalToString (mctx : MetavarContext) (g : MVarId) : MetaM String := do
  let decl ← g.getDecl
  let pp ← PrettyPrinter.ppExpr decl.type
  pure pp.pretty

private def goalsToStateString (mctx : MetavarContext) (gs : List MVarId) : MetaM String := do
  let rendered ← gs.mapM (goalToString mctx)
  pure <| String.intercalate "\n---\n" rendered

private def extractFromTacticInfo
  (moduleSource theoremSource : String)
  (mctx : MetavarContext)
  (tinfo : TacticInfo)
  : MetaM (Option TransitionRow) := do
  let goalsBefore := tinfo.goalsBefore.toList
  if goalsBefore.isEmpty then
    return none

  let before ← goalsToStateString mctx goalsBefore
  let tacticRaw := s!"{tinfo.stx}"
  let afterRows ← tinfo.goalsAfter.toList.mapM (goalToString mctx)

  return some {
    moduleSource := moduleSource
    theoremSource := theoremSource
    goalBefore := before
    tacticRaw := tacticRaw
    goalsAfter := afterRows
  }

/--
  TODO (integration):
  1. Traverse the per-declaration InfoTree.
  2. Find TacticInfo nodes.
  3. Call extractFromTacticInfo.
  4. Emit JSONL rows matching transition_schema.json.
-/
def emitTransitionsForDeclaration (_declName : Name) : CoreM Unit := do
  pure ()

end NexusMathlibExtractor
