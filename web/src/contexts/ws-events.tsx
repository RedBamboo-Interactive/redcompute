import { createContext, useContext, useEffect, useRef } from "react"
import type { WsEvent } from "@/api/types"

type Handler = (event: WsEvent) => void

export interface WsEventContextValue {
  subscribe: (handler: Handler) => () => void
  dispatch: (event: WsEvent) => void
}

export const WsEventContext = createContext<WsEventContextValue>({
  subscribe: () => () => {},
  dispatch: () => {},
})

export function useWsSubscribe(handler: Handler) {
  const { subscribe } = useContext(WsEventContext)
  const handlerRef = useRef(handler)
  handlerRef.current = handler

  useEffect(() => {
    return subscribe((e) => handlerRef.current(e))
  }, [subscribe])
}
