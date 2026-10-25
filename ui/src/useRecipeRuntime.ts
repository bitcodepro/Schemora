import { useEffect, useRef, useState } from 'react';
import type { ChangeEvent } from 'react';
import { errorMessage, request } from './api';
import { t } from './i18n.ts';
import type { ParsedInput, RecipeDefinition, RecipeEntry, RenderResult, SqlStrategy } from './recipeTypes';

const maximumFileBytes = 8 * 1024 * 1024;

/** Runtime state of the open recipe. Values stay in component memory and POST bodies only. */
export function useRecipeRuntime(draft: RecipeDefinition | null, entry: RecipeEntry | undefined, dirty: boolean) {
  const [inputText, setInputText] = useState<Record<string, string>>({});
  const [parsed, setParsed] = useState<Record<string, ParsedInput>>(Object.create(null));
  const [parseBusy, setParseBusy] = useState(false);
  const [renderBusy, setRenderBusy] = useState(false);
  const [runtimeError, setRuntimeError] = useState('');
  const [rendered, setRendered] = useState<RenderResult | null>(null);
  const [mode, setModeState] = useState<'standalone' | 'parameterized'>('standalone');
  const [strategy, setStrategyState] = useState<SqlStrategy>('auto');
  const [acknowledgeReview, setAcknowledgeReview] = useState(false);
  const [outputView, setOutputView] = useState<'sql' | 'example' | 'dapper'>('sql');
  const [copyState, setCopyState] = useState('');
  const runtimeRevision = useRef(0);
  const parseController = useRef<AbortController | null>(null);
  const renderController = useRef<AbortController | null>(null);
  useEffect(
    () => () => {
      parseController.current?.abort();
      renderController.current?.abort();
    },
    [],
  );

  function invalidateRuntime(clearValues = false) {
    runtimeRevision.current++;
    parseController.current?.abort();
    renderController.current?.abort();
    setParsed(Object.create(null));
    setRendered(null);
    setParseBusy(false);
    setRenderBusy(false);
    setRuntimeError('');
    setCopyState('');
    if (clearValues) {
      setInputText({});
      setAcknowledgeReview(false);
    }
  }
  function runtimeValue(name: string) {
    return Object.hasOwn(inputText, name) ? inputText[name] : '';
  }
  function changeRuntimeInput(name: string, text: string) {
    if (text.length > 8 * 1024 * 1024) {
      setRuntimeError(t('The list exceeds 8 million characters. The paste was rejected.'));
      return;
    }
    invalidateRuntime();
    setInputText(previous => ({ ...previous, [name]: text }));
  }
  // A saved value set replaces every input, so inputs it does not name become empty.
  function replaceRuntimeInputs(values: Record<string, string>) {
    invalidateRuntime();
    setInputText(Object.fromEntries((draft?.inputs || []).map(input => [input.name, values[input.name] ?? ''])));
  }
  async function importIds(event: ChangeEvent<HTMLInputElement>, name: string) {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    if (file.size > maximumFileBytes) {
      setRuntimeError(t('The ID file exceeds 8 MiB. Its contents were not loaded.'));
      return;
    }
    const revision = runtimeRevision.current;
    try {
      const contents = await file.text();
      if (runtimeRevision.current === revision) changeRuntimeInput(name, contents);
    } catch (cause) {
      setRuntimeError(errorMessage(cause, t('Could not read the ID file.')));
    }
  }
  async function parseInputs() {
    if (!draft) return;
    invalidateRuntime();
    const controller = new AbortController();
    parseController.current = controller;
    const revision = runtimeRevision.current;
    const result: Record<string, ParsedInput> = Object.create(null);
    setParseBusy(true);
    try {
      for (const input of draft.inputs)
        result[input.name] = await request<ParsedInput>('/api/inputs/parse', {
          method: 'POST',
          body: JSON.stringify({ text: runtimeValue(input.name), input }),
          signal: controller.signal,
        });
      if (!controller.signal.aborted && runtimeRevision.current === revision) setParsed(result);
    } catch (cause) {
      if (!controller.signal.aborted) setRuntimeError(errorMessage(cause, t('Could not validate the ID list.')));
    } finally {
      if (runtimeRevision.current === revision) setParseBusy(false);
    }
  }
  const allInputsChecked = Boolean(draft && draft.inputs.every(input => parsed[input.name] && parsed[input.name].statistics.invalid === 0));
  const invalidCount = Object.values(parsed).reduce((total, input) => total + input.statistics.invalid, 0);
  const canRender = Boolean(
    entry &&
      !dirty &&
      allInputsChecked &&
      entry.validation.status !== 'Broken' &&
      (entry.validation.status !== 'NeedsReview' || acknowledgeReview),
  );
  async function renderRecipe() {
    if (!canRender || !entry) return;
    renderController.current?.abort();
    const controller = new AbortController();
    renderController.current = controller;
    const revision = runtimeRevision.current;
    setRenderBusy(true);
    setRuntimeError('');
    setRendered(null);
    setCopyState('');
    try {
      const body = JSON.stringify({
        inputText: Object.fromEntries((draft?.inputs || []).map(input => [input.name, runtimeValue(input.name)])),
        mode,
        strategy,
        acknowledgeReview,
        expectedVersion: entry.definition.version,
      });
      if (new Blob([body]).size > 16 * 1024 * 1024) throw new Error(t('The combined parameter size exceeds 16 MiB.'));
      const result = await request<RenderResult>(`/api/recipes/${encodeURIComponent(entry.definition.id)}/render`, {
        method: 'POST',
        body,
        signal: controller.signal,
      });
      if (!controller.signal.aborted && runtimeRevision.current === revision) {
        setRendered(result);
        setOutputView(mode === 'parameterized' && result.parameterizedExample ? 'example' : 'sql');
      }
    } catch (cause) {
      if (!controller.signal.aborted) setRuntimeError(errorMessage(cause, t('SQL was not generated.')));
    } finally {
      if (runtimeRevision.current === revision) setRenderBusy(false);
    }
  }
  // Changing the output shape discards SQL rendered for the previous one.
  function setMode(value: 'standalone' | 'parameterized') {
    setModeState(value);
    renderController.current?.abort();
    setRendered(null);
  }
  function setStrategy(value: SqlStrategy) {
    setStrategyState(value);
    renderController.current?.abort();
    setRendered(null);
  }
  const output = rendered
    ? outputView === 'dapper'
      ? rendered.dapperExample || rendered.sql
      : outputView === 'example'
        ? rendered.parameterizedExample || rendered.sql
        : rendered.sql
    : '';
  async function copyOutput() {
    if (!output) return;
    try {
      await navigator.clipboard.writeText(output);
      setCopyState(t('Copied to clipboard.'));
    } catch {
      setCopyState(t('Copying is unavailable. Select the text and copy it manually.'));
    }
  }

  return {
    parsed,
    parseBusy,
    renderBusy,
    runtimeError,
    setRuntimeError,
    rendered,
    setRendered,
    mode,
    setMode,
    strategy,
    setStrategy,
    acknowledgeReview,
    setAcknowledgeReview,
    outputView,
    setOutputView,
    copyState,
    invalidateRuntime,
    runtimeValue,
    changeRuntimeInput,
    replaceRuntimeInputs,
    importIds,
    parseInputs,
    invalidCount,
    canRender,
    renderRecipe,
    output,
    copyOutput,
  };
}

export type RecipeRuntime = ReturnType<typeof useRecipeRuntime>;
