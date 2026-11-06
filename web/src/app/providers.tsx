"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";
import { AuthProvider } from "@/lib/auth";
import { I18nProvider } from "@/lib/i18n";
import { ApiError } from "@/lib/api";

export function Providers({ children }: { children: React.ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 2_000,
            // Don't retry auth / not-found errors; do retry network blips.
            retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
          },
        },
      }),
  );

  return (
    <QueryClientProvider client={client}>
      <I18nProvider>
        <AuthProvider>{children}</AuthProvider>
      </I18nProvider>
    </QueryClientProvider>
  );
}
