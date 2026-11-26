import type { Profile } from '../shared/types';

/**
 * What to do with a certificate Chromium could not verify.
 *   accept  the user pinned exactly this certificate for this server
 *   prompt  the user allowed self-signed for this server but nothing is pinned yet: ask, showing the fingerprint
 *   reject  anything else (including a pinned server presenting a different certificate: that is what a man in the middle looks like)
 */
export type CertDecision = 'accept' | 'prompt' | 'reject';

export function decideCertificate(profile: Pick<Profile, 'origin' | 'allowSelfSigned' | 'pinnedCert'>, hostname: string, fingerprint: string): CertDecision {
  if (!profile.allowSelfSigned) return 'reject';
  let host: string;
  try {
    host = new URL(profile.origin).hostname;
  } catch {
    return 'reject';
  }
  if (host.toLowerCase() !== hostname.toLowerCase()) return 'reject';
  if (!profile.pinnedCert) return 'prompt';
  return profile.pinnedCert === fingerprint ? 'accept' : 'reject';
}
