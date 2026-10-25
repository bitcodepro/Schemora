export type SchemaObjectKind = 'table' | 'view' | 'procedure' | 'scalarFunction' | 'tableFunction' | 'synonym';
export type SchemaParameter = { name: string; dataType: string; isOutput: boolean; isNullable: boolean };
export type UniqueKey = { id: string; name: string; columns: string[] };
export type SchemaObject = {
  id: string;
  schema: string;
  name: string;
  kind: SchemaObjectKind;
  primaryKey?: string[];
  columns?: { name: string; dataType: string; isNullable: boolean }[];
  uniqueKeys?: UniqueKey[];
  parameters?: SchemaParameter[];
  returnType?: string;
  target?: string;
  definition?: string;
  sourcePath?: string;
  line?: number;
  isPartial?: boolean;
};
export type SchemaIndex = {
  id: string;
  objectId: string;
  name: string;
  kind: string;
  isUnique: boolean;
  isPrimaryKey: boolean;
  columns: { name: string; descending: boolean }[];
  includedColumns?: string[];
  filter?: string;
};
export type ObjectDependency = {
  id: string;
  fromObjectId: string;
  toObjectId?: string;
  targetName: string;
  kind: string;
  sourcePath?: string;
  line?: number;
  confidence: number;
};
export type CatalogObject = SchemaObject & { databaseId: string; databaseName: string };
export type ObjectPage = { items: CatalogObject[]; total: number; offset: number; limit: number };
export type ObjectDetail = CatalogObject & {
  indexes?: SchemaIndex[];
  dependencies?: ObjectDependency[];
  dependents?: ObjectDependency[];
  impact?: { objectIds: string[]; recipeIds: string[] };
};
export const objectKindNames: Record<SchemaObjectKind, string> = {
  table: 'Таблица',
  view: 'Представление',
  procedure: 'Процедура',
  scalarFunction: 'Скалярная функция',
  tableFunction: 'Табличная функция',
  synonym: 'Синоним',
};
