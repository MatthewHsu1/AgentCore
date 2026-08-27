import type * as React from "react";
import { Sidebar, SidebarContent, SidebarRail } from "@/components/ui/sidebar";
import { ThreadList } from "@/components/assistant-ui/thread-list";

/**
 * The sidebar, holding the thread list and nothing else.
 *
 * This stands in for the vendored `components/assistant-ui/threadlist-sidebar.tsx`, which is a
 * seventy-line shell whose only substance is the `ThreadList` below. The rest of it is a header
 * linking to assistant-ui.com and a footer linking to their repository — right for their template,
 * wrong for a UI a consumer of this library serves to their own callers.
 *
 * Copying the shell rather than editing theirs is what keeps `npx shadcn add` a clean overwrite.
 * Their file stays on disk and stays re-pullable; nothing imports it, so it never reaches the
 * bundle.
 *
 * `ThreadList` already carries the new-thread control and the search box, so there is no header to
 * replace — dropping theirs leaves nothing missing.
 */
export function AgentCoreSidebar({
  ...props
}: React.ComponentProps<typeof Sidebar>) {
  return (
    <Sidebar {...props}>
      <SidebarContent className="px-2 py-2">
        <ThreadList />
      </SidebarContent>
      {/* The drag handle that resizes and collapses the sidebar. Part of the shadcn sidebar
          contract rather than decoration: without it the collapse control has nothing to grab. */}
      <SidebarRail />
    </Sidebar>
  );
}
