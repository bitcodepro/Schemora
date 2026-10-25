Synthetic complex SQL repository. Tests create External.dacpac from references/External.sql in an isolated fixture copy, then build Main with sibling ProjectReference, imported props/targets, DACPAC reference, SQLCMD variable and external :r include. Deployment scripts are packaged for dependency testing and are never executed by these build tests.

