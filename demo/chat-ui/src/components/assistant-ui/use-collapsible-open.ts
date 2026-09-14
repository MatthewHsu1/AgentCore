import { useCallback, useRef, useState } from "react";
import { useScrollLock } from "@assistant-ui/react";

type ControlledOpen = {
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  defaultOpen?: boolean;
  lockDurationMs: number;
};

/**
 * One Collapsible's open state, shared by ToolGroupRoot and ToolFallbackRoot.
 * A single hook keeps the controlled/uncontrolled split and the scroll lock in
 * lockstep; two copies already drifted once.
 */
export function useControlledOpen({
  open: controlledOpen,
  onOpenChange,
  defaultOpen = false,
  lockDurationMs,
}: ControlledOpen) {
  const ref = useRef<HTMLDivElement>(null);
  const [uncontrolledOpen, setUncontrolledOpen] = useState(defaultOpen);
  const lockScroll = useScrollLock(ref, lockDurationMs);

  const isControlled = controlledOpen !== undefined;
  const isOpen = isControlled ? controlledOpen : uncontrolledOpen;

  const handleOpenChange = useCallback(
    (open: boolean) => {
      lockScroll();
      if (!isControlled) {
        setUncontrolledOpen(open);
      }
      onOpenChange?.(open);
    },
    [lockScroll, isControlled, onOpenChange],
  );

  return { ref, isOpen, handleOpenChange };
}
