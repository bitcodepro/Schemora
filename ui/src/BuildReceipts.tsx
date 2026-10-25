import { t } from './i18n';
export type BuildReceipt = {
  databaseId: string;
  snapshotId: string;
  manifestId: string;
  toolchain?: {
    kind: string;
    executable: string;
    executableSha256: string;
    sdkVersion?: string;
    msBuildVersion: string;
    msBuildSha256?: string;
    ssdtTargets?: string;
    ssdtTargetsSha256?: string;
    ssdtTaskVersion?: string;
    sqlProjectSdk?: string;
    ssdtTaskSha256?: string;
  };
};
export default function BuildReceipts({ builds }: { builds?: BuildReceipt[] }) {
  return (
    <details>
      <summary>{t('Recorded build toolchain')}</summary>
      <p>{t('Versions are recorded by completed builds. Imported DACPACs and earlier builds may have no tool receipt.')}</p>
      {!builds?.length && <p>{t('No recorded build toolchain')}</p>}
      {builds?.map(b => (
        <article key={b.databaseId}>
          <p>
            {b.databaseId} · {t('Snapshots')}: {b.snapshotId}
          </p>
          <code>{b.manifestId}</code>
          {b.toolchain ? (
            <dl>
              <dt>{t('Build kind')}</dt>
              <dd>{b.toolchain.kind}</dd>
              <dt>.NET SDK</dt>
              <dd>{b.toolchain.sdkVersion || t('Not recorded or not applicable')}</dd>
              <dt>SQL SDK</dt>
              <dd>{b.toolchain.sqlProjectSdk || t('Not recorded or not applicable')}</dd>
              <dt>MSBuild</dt>
              <dd>{b.toolchain.msBuildVersion}</dd>
              <dt>{t('Executable')}</dt>
              <dd>
                <code>{b.toolchain.executable}</code>
              </dd>
              <dt>SHA256</dt>
              <dd>
                <code>{b.toolchain.executableSha256}</code>
              </dd>
              <dt>MSBuild SHA256</dt>
              <dd>
                <code>{b.toolchain.msBuildSha256 || t('Not recorded or not applicable')}</code>
              </dd>
              <dt>SSDT</dt>
              <dd>{b.toolchain.ssdtTaskVersion || t('Not recorded or not applicable')}</dd>
              <dt>SSDT targets</dt>
              <dd>
                <code>{b.toolchain.ssdtTargets || t('Not recorded or not applicable')}</code>
              </dd>
              <dt>SSDT SHA256</dt>
              <dd>
                <code>{b.toolchain.ssdtTargetsSha256 || t('Not recorded or not applicable')}</code>
              </dd>
            </dl>
          ) : (
            <p>{t('No recorded build toolchain')}</p>
          )}
        </article>
      ))}
    </details>
  );
}
