import FormalConjecturesUtil
import FormalConjectures.Subsets.FC100SolvedSet1

/-! Stripped stub for proof reconstruction: Mathoverflow10799. -/

theorem type to differ between builds. match expectedType? with | some ty => mkCanonicalSorryAnnotation ty | none => elabTermAndAnnotate a expectedType? true else elabTermAndAnnotate a expectedType? true | _ => Elab.throwUnsupportedSyntax -- TODO: add delaborator (for the auxiliary declaration mode!) open InfoTree /-- An answer: a term, and the context in which it was elaborated -/ structure AnswerInfo where ctx : Elab.ContextInfo term : Elab.TermInfo /-- Print an answer -/ def AnswerInfo.format (a : AnswerInfo) : Elab.Term.TermElabM MessageData := by
  sorry
