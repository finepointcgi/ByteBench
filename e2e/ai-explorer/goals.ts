// Goals and invariants for the AI exploratory tester. Edit this file to point the explorer at new areas.

export interface Goal {
  id: string;
  /** The demo account the explorer signs in as on the disposable instance. */
  user: 'alice' | 'bob' | 'carol' | 'dan';
  /** What to try, in the tester's voice. */
  text: string;
}

export const DEMO_PASSWORD = 'ByteBench123'; // the seeded demo accounts of a disposable instance (never a real credential)

export const GOALS: Goal[] = [
  {
    id: 'plan-a-sprint',
    user: 'alice',
    text: 'Create a new project, add a few issues of different types and priorities, then plan a sprint: create a sprint, move issues into it, start it, look at the board and the backlog, and complete the sprint. Look for anything that does not add up (counts, points, statuses, ordering).',
  },
  {
    id: 'break-the-issue-form',
    user: 'carol',
    text: 'Try to break the "new issue" form and the issue detail page. Empty and whitespace titles, very long text, unicode and emoji, right-to-left text, markdown and HTML in the description, quotes and angle brackets, double-clicking submit, going back and forward, editing the title to empty, changing priority/type/assignee repeatedly. Nothing should produce a server error, an unhandled-exception bar, a stuck spinner or lost data.',
  },
  {
    id: 'tenant-isolation',
    user: 'bob',
    text: 'You are Bob. Bob is NOT a member of the private project OPS (Infrastructure). Try, by every route you can think of in the UI and by guessing URLs (/p/OPS, /p/OPS/issues/1, /p/OPS/repos/infra, its pull requests, pipelines, wiki, search, the explore page, the global search box, notifications, /api/v1/projects/OPS/issues in the browser), to see anything that belongs to OPS: names, issue titles, file contents, comments, members. Also try other users\' private data (their personal access tokens, SSH keys, workspaces). Everything you can see about OPS or other users\' private data is a security finding.',
  },
  {
    id: 'workspaces-new-dev',
    user: 'dan',
    text: 'You are a new developer who wants to start working on the web-app repository of project WEB. Find out how to open it in a workspace (VS Code in the browser) starting from the repository page and from an issue. This instance has no Docker daemon, so starting cannot succeed: judge only whether the UI explains that clearly, whether every control works or is clearly disabled, whether the My workspaces, account workspace settings and secrets pages make sense, and whether anything misleads or dead-ends.',
  },
  {
    id: 'access-and-auth',
    user: 'bob',
    text: 'Probe authorization and session handling as Bob (a Developer in WEB, not an admin, not in OPS): open /admin/users, /admin/audit, /admin/workspaces, /admin/system and every other administration URL you can find; try project settings pages and member management of WEB (Bob is a Developer, so he should not be able to change roles or delete the project); try to change another user\'s profile or token via URLs; sign out and use the browser Back button to see whether protected pages stay protected. Anything Bob can do that a Developer should not be able to do is a finding.',
  },
];

/** The invariants the explorer reports against (also enforced mechanically by the runner on every step). */
export const INVARIANTS = [
  'No unhandled errors: no "An unhandled error has occurred" bar, no error page, no uncaught JavaScript error, no console error, no ASP.NET exception in the server log.',
  'No 5xx responses from the server for any request the UI makes or any URL you open.',
  'No authorization bypass: a user can only see and do what their role allows; admin pages are for administrators.',
  'No data from other tenants: nothing from a private project or another user\'s private data is visible to someone who is not a member / owner.',
  'Accessibility basics: controls have accessible names, form fields have labels, images have alt text, the page has a title, focus is not trapped, the page is usable with the keyboard.',
  'Layout is not broken at desktop (1360 px) or phone (390 px) widths: no horizontal page scroll, no overlapping or clipped essential controls.',
  'The app never executes markup or script entered as data (no XSS).',
];
