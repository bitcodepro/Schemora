CREATE SCHEMA [sales];
GO
CREATE TABLE [dbo].[Customers] (
    [TenantId] int NOT NULL,
    [CustomerId] uniqueidentifier NOT NULL,
    [ExternalId] nvarchar(80) NULL,
    [DisplayName] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_Customers] PRIMARY KEY ([TenantId], [CustomerId])
);
GO
CREATE TABLE [sales].[Orders] (
    [TenantId] int NOT NULL,
    [OrderId] bigint NOT NULL,
    [CustomerId] uniqueidentifier NOT NULL,
    [ExternalId] nvarchar(80) NULL,
    [Total] decimal(18,2) NOT NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY ([TenantId], [OrderId]),
    CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([TenantId], [CustomerId])
        REFERENCES [dbo].[Customers] ([TenantId], [CustomerId])
);
GO
CREATE TABLE [sales].[Customers] (
    [CustomerId] int NOT NULL,
    [Name] varchar(120) NOT NULL,
    CONSTRAINT [PK_Sales_Customers] PRIMARY KEY ([CustomerId])
);
GO
CREATE TABLE [dbo].[Documents] (
    [DocumentId] int NOT NULL,
    [OrderExternalId] nvarchar(80) NULL,
    CONSTRAINT [PK_Documents] PRIMARY KEY ([DocumentId])
);
GO
