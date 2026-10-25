CREATE TABLE dbo.MainItems(Id bigint NOT NULL PRIMARY KEY,SharedCode int NULL);
GO
CREATE VIEW dbo.SharedCodeView AS SELECT Code FROM [$(SharedDatabase)].dbo.SharedCodes;
GO
CREATE VIEW dbo.ExternalCodeView AS SELECT Id FROM [ExternalDatabase].dbo.ExternalCodes;
