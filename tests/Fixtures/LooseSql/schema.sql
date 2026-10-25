-- A standalone schema, without a SQL project. Static parsing is a later-phase fallback.
CREATE TABLE [dbo].[LooseItems] ([Id] int NOT NULL PRIMARY KEY, [Name] nvarchar(100) NULL);
GO
