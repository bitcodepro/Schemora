import { t } from './i18n.ts';
export type InputType = 'int' | 'bigint' | 'uniqueidentifier' | 'nvarchar' | 'varchar' | 'date' | 'datetime2' | 'bit' | 'decimal';
export type SqlStrategy = 'auto' | 'values' | 'temp' | 'openjson' | 'list';
export type RecipeInput = {
  name: string;
  type: InputType;
  maxLength?: number;
  encoding?: string;
  collation?: string;
  precision?: number;
  scale?: number;
  deduplicate: boolean;
  preserveOrder: boolean;
  strategy: SqlStrategy;
  /** Only for {{value:...}} inputs: an empty value renders as a typed NULL. */
  optional?: boolean;
};
export type RecipeDependency = { databaseId: string; tableId: string; columnName?: string; relationId?: string };
export type RecipeDefinition = {
  id: string;
  name: string;
  description: string;
  tags: string[];
  dialect: 'sqlserver';
  databaseIds: string[];
  version: number;
  sqlTemplate: string;
  inputs: RecipeInput[];
  dependencies?: RecipeDependency[];
  compatibilityLevel: number;
};
export type RecipeDiagnostic = { severity: string; code: string; message: string };
export type RecipeEntry = {
  workspaceId: string;
  definition: RecipeDefinition;
  validation: { status: 'Valid' | 'Warning' | 'Broken' | 'NeedsReview'; dependencies: RecipeDependency[]; diagnostics: RecipeDiagnostic[] };
  createdAt: string;
  updatedAt: string;
  lastCompatibleSnapshots: Record<string, string>;
};
export type RecipeVersion = {
  id: string;
  recipeId: string;
  version: number;
  definition: RecipeDefinition;
  createdAt: string;
  reason: string;
};
export type LineageItem = {
  databaseId: string;
  databaseName: string;
  tableId: string;
  schema: string;
  tableName: string;
  columnName?: string;
  relationId?: string;
};
export type InputStatistics = { total: number; valid: number; invalid: number; unique: number; duplicates: number; outputCount: number };
export type ParsedInput = {
  input: RecipeInput;
  statistics: InputStatistics;
  values: { index: number; line: number; column: number; value: string | number }[];
  errors: { index: number; line: number; column: number; code: string; message: string; rawValue?: string }[];
};
export type RenderResult = {
  sql: string;
  mode: 'standalone' | 'parameterized';
  strategy: string;
  statistics: Record<string, InputStatistics>;
  warnings: string[];
  parameters: { name: string; sqlType: string; value: unknown }[];
  parameterizedExample?: string;
  dapperExample?: string;
  recipeVersion?: number;
};
export type RecipeTestResult = { success: boolean; checks: number | unknown[]; diagnostics: RecipeDiagnostic[]; testedAt: string };
export type RecipeSeed = {
  workspaceId: string;
  name: string;
  sqlTemplate: string;
  databaseIds: string[];
  inputs?: RecipeInput[];
  definition?: RecipeDefinition;
};

export function defaultInput(name = 'Ids', type: InputType = 'bigint'): RecipeInput {
  return {
    name,
    type,
    ...(type === 'nvarchar' || type === 'varchar' ? { maxLength: 100 } : {}),
    ...(type === 'varchar' ? { encoding: 'ascii' } : {}),
    ...(type === 'decimal' ? { precision: 18, scale: 2 } : {}),
    deduplicate: false,
    preserveOrder: true,
    strategy: 'auto',
  };
}

export function emptyRecipe(databaseIds: string[] = []): RecipeDefinition {
  return {
    id: '',
    name: '',
    description: '',
    tags: [],
    dialect: 'sqlserver',
    databaseIds,
    version: 0,
    sqlTemplate: '',
    inputs: [defaultInput()],
    dependencies: [],
    compatibilityLevel: 130,
  };
}

// Authoring payload is deliberately whitelisted; runtime text, parsed values and rendered SQL have no path into it.
export function recipeForSave(recipe: RecipeDefinition): RecipeDefinition {
  return {
    id: recipe.id,
    name: recipe.name,
    description: recipe.description || '',
    tags: [...recipe.tags],
    dialect: 'sqlserver',
    databaseIds: [...recipe.databaseIds],
    version: recipe.version,
    sqlTemplate: recipe.sqlTemplate,
    inputs: recipe.inputs.map(input => ({
      name: input.name,
      type: input.type,
      maxLength: input.maxLength,
      encoding: input.encoding,
      collation: input.collation,
      precision: input.precision,
      scale: input.scale,
      deduplicate: input.deduplicate ?? false,
      preserveOrder: input.preserveOrder ?? true,
      strategy: input.strategy || 'auto',
      optional: input.optional ?? false,
    })),
    dependencies: recipe.dependencies?.map(dependency => ({
      databaseId: dependency.databaseId,
      tableId: dependency.tableId,
      columnName: dependency.columnName,
      relationId: dependency.relationId,
    })),
    compatibilityLevel: recipe.compatibilityLevel || 130,
  };
}

export function inputFromSqlType(name: string, sqlType: string): RecipeInput | null {
  const normalized = sqlType.replace(/[\[\]]/g, '').toLowerCase();
  const base = normalized.match(/^\s*(\w+)/)?.[1];
  const type: InputType | undefined =
    base === 'tinyint' || base === 'smallint'
      ? 'int'
      : base === 'datetime' || base === 'smalldatetime'
        ? 'datetime2'
        : base === 'numeric' || base === 'money' || base === 'smallmoney'
          ? 'decimal'
          : ['int', 'bigint', 'uniqueidentifier', 'nvarchar', 'varchar', 'date', 'datetime2', 'bit', 'decimal'].includes(base || '')
            ? (base as InputType)
            : undefined;
  if (!type) return null;
  const input = defaultInput(name, type);
  if (type === 'decimal') {
    const [, precision, scale] = normalized.match(/\((\d+)\s*(?:,\s*(\d+))?\)/) || [];
    input.precision = base === 'money' ? 19 : base === 'smallmoney' ? 10 : precision ? Number(precision) : 18;
    input.scale = base === 'money' || base === 'smallmoney' ? 4 : scale ? Number(scale) : 0;
  }
  if (type === 'nvarchar' || type === 'varchar') {
    const length = normalized.match(/\((\d+)\)/)?.[1];
    if (length) input.maxLength = Number(length);
    else if (normalized.includes('(max)')) input.maxLength = -1;
  }
  return input;
}

// Mirrors the engine's placeholder grammar closely enough for UI hints; the server remains the authority on templates.
export function valueInputNames(template: string): Set<string> {
  return new Set([...template.matchAll(/\{\{value:([A-Za-z][A-Za-z0-9_]{0,63})\}\}/g)].map(match => match[1]));
}

export function valueFormatHint(type: InputType): string {
  return type === 'date'
    ? 'yyyy-MM-dd'
    : type === 'datetime2'
      ? 'yyyy-MM-ddTHH:mm'
      : type === 'bit'
        ? '1 / 0'
        : type === 'uniqueidentifier'
          ? '00000000-0000-0000-0000-000000000000'
          : type === 'decimal'
            ? '1500.50'
            : '';
}

export type DatePreset = { label: string; value: string };

/** Common period boundaries in the user's local calendar, written in the ISO form the engine accepts. */
export function datePresets(type: InputType, now: Date = new Date()): DatePreset[] {
  if (type !== 'date' && type !== 'datetime2') return [];
  const iso = (date: Date) =>
    `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` +
    (type === 'datetime2' ? 'T00:00' : '');
  const day = (offset: number) => new Date(now.getFullYear(), now.getMonth(), now.getDate() + offset);
  return [
    { label: 'Today', value: iso(day(0)) },
    { label: 'Tomorrow', value: iso(day(1)) },
    { label: 'Start of this month', value: iso(new Date(now.getFullYear(), now.getMonth(), 1)) },
    { label: 'Start of last month', value: iso(new Date(now.getFullYear(), now.getMonth() - 1, 1)) },
    { label: '30 days ago', value: iso(day(-30)) },
    { label: 'Start of this year', value: iso(new Date(now.getFullYear(), 0, 1)) },
  ];
}

export type PeriodPreset = { label: string; from: Date; to: Date };

/** Whole periods with an exclusive end (the next period's first day), matching templates like `>= From AND < To`. */
export function periodPresets(now: Date = new Date()): PeriodPreset[] {
  const y = now.getFullYear();
  const m = now.getMonth();
  const today = new Date(y, m, now.getDate());
  const tomorrow = new Date(y, m, now.getDate() + 1);
  return [
    { label: 'Last month', from: new Date(y, m - 1, 1), to: new Date(y, m, 1) },
    { label: 'This month', from: new Date(y, m, 1), to: new Date(y, m + 1, 1) },
    { label: 'Last 7 days', from: new Date(y, m, today.getDate() - 7), to: tomorrow },
    { label: 'Last 30 days', from: new Date(y, m, today.getDate() - 30), to: tomorrow },
    { label: 'Last year', from: new Date(y - 1, 0, 1), to: new Date(y, 0, 1) },
    { label: 'This year', from: new Date(y, 0, 1), to: new Date(y + 1, 0, 1) },
  ];
}

export function isoDate(date: Date, type: InputType): string {
  return (
    `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` +
    (type === 'datetime2' ? 'T00:00' : '')
  );
}

export function quoteSqlIdentifier(name: string): string {
  return `[${name.replaceAll(']', ']]')}]`;
}

export function decodeRecipeImport(value: unknown): RecipeDefinition {
  if (!value || typeof value !== 'object') throw new Error(t('JSON должен содержать определение рецепта.'));
  const envelope = value as { recipe?: unknown };
  const recipe = (envelope.recipe || value) as RecipeDefinition;
  if (
    typeof recipe.name !== 'string' ||
    typeof recipe.sqlTemplate !== 'string' ||
    !Array.isArray(recipe.tags) ||
    !Array.isArray(recipe.databaseIds) ||
    !Array.isArray(recipe.inputs)
  )
    throw new Error(t('В JSON отсутствуют name, sqlTemplate, tags, databaseIds или inputs.'));
  return recipeForSave(recipe);
}
