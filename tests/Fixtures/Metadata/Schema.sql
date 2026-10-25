CREATE TABLE [dbo].[Categories] (
    [Id] int NOT NULL CONSTRAINT [PK_Categories] PRIMARY KEY,
    [Name] nvarchar(80) NOT NULL
);
GO
CREATE TABLE [dbo].[Items] (
    [Id] int NOT NULL CONSTRAINT [PK_Items] PRIMARY KEY,
    [CategoryId] int NULL,
    [Code] varchar(24) NOT NULL,
    [Payload] nvarchar(120) NULL,
    CONSTRAINT [UQ_Items_Code] UNIQUE ([Code]),
    CONSTRAINT [FK_Items_Categories] FOREIGN KEY ([CategoryId]) REFERENCES [dbo].[Categories]([Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_Items_Category] ON [dbo].[Items] ([CategoryId] DESC,[Code] ASC) INCLUDE ([Payload]) WHERE [CategoryId] IS NOT NULL;
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Items_CategoryCode] ON [dbo].[Items] ([CategoryId],[Code]);
GO
CREATE VIEW [dbo].[ItemOverview] AS
    SELECT i.[Id],i.[Code],c.[Name] AS [CategoryName] FROM [dbo].[Items] i LEFT JOIN [dbo].[Categories] c ON c.[Id]=i.[CategoryId];
GO
CREATE VIEW [dbo].[ItemKeys] WITH SCHEMABINDING AS SELECT [Id],[Code] FROM [dbo].[Items];
GO
CREATE UNIQUE CLUSTERED INDEX [CIX_ItemKeys] ON [dbo].[ItemKeys]([Id]);
GO
CREATE FUNCTION [dbo].[FormatCode](@code varchar(24),@amount decimal(9,2)) RETURNS nvarchar(80)
AS BEGIN RETURN CONVERT(nvarchar(80),CONCAT(@code,N':',@amount)); END;
GO
CREATE FUNCTION [dbo].[ItemsForCategory](@categoryId int) RETURNS TABLE
AS RETURN (SELECT [Id],[Code],[CategoryId] FROM [dbo].[Items] WHERE [CategoryId]=@categoryId);
GO
CREATE FUNCTION [dbo].[ItemTokens](@id int) RETURNS @result TABLE ([Token] nvarchar(40) NOT NULL)
AS BEGIN INSERT INTO @result([Token]) SELECT CONVERT(nvarchar(40),[Code]) FROM [dbo].[Items] WHERE [Id]=@id; RETURN; END;
GO
CREATE VIEW [dbo].[FormattedItems] AS SELECT [Id],[dbo].[FormatCode]([Code],12.30) AS [Formatted] FROM [dbo].[Items];
GO
CREATE PROCEDURE [dbo].[GetItems] @minId int=0,@code varchar(24)=NULL,@total int OUTPUT
AS BEGIN
    SELECT @total=COUNT(*) FROM [dbo].[Items] WHERE [Id]>=@minId;
    SELECT [Id],[Code] FROM [dbo].[Items] WHERE [Id]>=@minId;
END;
GO
CREATE SYNONYM [dbo].[ItemsAlias] FOR [dbo].[Items];
GO
