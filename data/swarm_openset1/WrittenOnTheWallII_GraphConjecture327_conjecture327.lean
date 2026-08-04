import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100OpenSet1
open SimpleGraph

/-! Open-problem stub: WrittenOnTheWallII.GraphConjecture327.conjecture327 → 'g_conjecture327_new' -/

namespace WrittenOnTheWallII.GraphConjecture327

theorem g_conjecture327_new : answer(False) ↔ ∀ (V : Type) [Fintype V] [DecidableEq V] (G : SimpleGraph V) [DecidableRel G.Adj] (_hG : G.Connected) (_h : 3 * G.dominationNumber = G.indepDominationNumber), IsWellTotallyDominated G := by
  sorry

end WrittenOnTheWallII.GraphConjecture327
