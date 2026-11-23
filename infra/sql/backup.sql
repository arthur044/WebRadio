/* =====================================================================
   WebRadio — infra/sql/backup.sql
   Autor: Atlas (DBA) · tarefa E5-A01 (09-epico-5-tarefas.md, E5-D3)

   BACKUP completo do WebRadio, rodado pelo serviço `backup` (E5-F03) como svc_backup (db_backupoperator, logins-backup.sql).
     sqlcmd -b -C -N -I -S sqlserver -U svc_backup -i /init/sql/backup.sql     (senha por SQLCMDPASSWORD, nunca -P)

   - WITH CHECKSUM: grava o checksum das páginas e RECONFERE o das páginas lidas; uma página corrompida no disco derruba o BACKUP.
     É a verificação que o svc_backup consegue fazer: RESTORE VERIFYONLY exige CREATE DATABASE, que ele não tem (E5-D3).
   - Sem COMPRESSION: a edição Express (padrão do compose) recusa ("BACKUP DATABASE WITH COMPRESSION is not supported on Express
     Edition"), e o restic já comprime antes do envio.
   - INIT: o arquivo é sobrescrito a cada rodada (um só backup no volume sql-backup; o histórico fica no restic).
   - Caminho FIXO, sem variável: o svc_backup poderia gravar em qualquer lugar onde o SQL Server escreve.
   - Recovery SIMPLE (init-db.sql): este é o ÚNICO backup; não há BACKUP LOG. Perda máxima = 1 dia.
   O backup.sh só pode seguir para o envio ao restic se este script sair com código 0.
   ===================================================================== */

:on error exit
SET NOCOUNT ON;

BACKUP DATABASE WebRadio
    TO DISK = N'/var/opt/mssql/backup/webradio.bak'
    WITH CHECKSUM, INIT, NAME = N'WebRadio-completo', STATS = 100;
GO
PRINT 'BACKUP_SQL_OK';
GO
