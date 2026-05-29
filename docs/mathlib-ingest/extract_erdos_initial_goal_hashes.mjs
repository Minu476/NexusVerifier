#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

function usage() {
  console.error('Usage: node extract_erdos_initial_goal_hashes.mjs <lean_dir> <out_csv>');
}

function sha256Hex(s) {
  return crypto.createHash('sha256').update(s, 'utf8').digest('hex');
}

function canonicalize(goal) {
  if (!goal || !goal.trim()) return '';
  let normalized = goal.trim().replace(/\s+/g, ' ');
  normalized = normalized.replace(/\bforall\s+[a-zA-Z][a-zA-Z0-9_']*/g, 'forall _');
  return normalized;
}

function csvEscape(s) {
  const v = String(s ?? '');
  return '"' + v.replaceAll('"', '""') + '"';
}

function extractTheoremStatement(text) {
  const lines = text.split(/\r?\n/);
  const theoremStart = lines.findIndex((line) => /^\s*theorem\s+[A-Za-z0-9_']+\s*:/.test(line));
  if (theoremStart < 0) return null;

  const firstLine = lines[theoremStart];
  const startMatch = firstLine.match(/^\s*theorem\s+([A-Za-z0-9_']+)\s*:(.*)$/);
  if (!startMatch) return null;

  const theoremName = startMatch[1];
  const chunks = [startMatch[2] ?? ''];
  for (let i = theoremStart + 1; i < lines.length; i += 1) {
    chunks.push(lines[i]);
    if (lines[i].includes(':= by')) break;
  }

  let statement = chunks.join(' ');
  const byIdx = statement.indexOf(':= by');
  if (byIdx >= 0) statement = statement.slice(0, byIdx);
  statement = statement.trim();
  if (!statement) return null;

  return { theoremName, statement };
}

const [, , leanDir, outCsv] = process.argv;
if (!leanDir || !outCsv) {
  usage();
  process.exit(1);
}

if (!fs.existsSync(leanDir) || !fs.statSync(leanDir).isDirectory()) {
  console.error(`Lean directory not found: ${leanDir}`);
  process.exit(2);
}

const files = fs.readdirSync(leanDir)
  .filter((f) => f.endsWith('.lean'))
  .sort((a, b) => a.localeCompare(b));

const rows = [];
for (const file of files) {
  const fullPath = path.join(leanDir, file);
  const text = fs.readFileSync(fullPath, 'utf8');
  const parsed = extractTheoremStatement(text);
  if (!parsed) continue;

  const problemId = `Erdos_${path.basename(file, '.lean')}`;
  const canonical = canonicalize(parsed.statement);
  const hashPlain = sha256Hex(canonical);
  const hashTurnstile = sha256Hex(`⊢ ${canonical}`);

  rows.push({
    problemId,
    theoremName: parsed.theoremName,
    canonical,
    hashPlain,
    hashTurnstile,
  });
}

const header = 'problem_id,theorem_name,canonical_goal,hash_plain,hash_turnstile';
const body = rows
  .map((r) => [
    csvEscape(r.problemId),
    csvEscape(r.theoremName),
    csvEscape(r.canonical),
    csvEscape(r.hashPlain),
    csvEscape(r.hashTurnstile),
  ].join(','))
  .join('\n');

const output = body ? `${header}\n${body}\n` : `${header}\n`;
fs.writeFileSync(outCsv, output, 'utf8');

console.error(`Wrote ${rows.length} rows to ${outCsv}`);
