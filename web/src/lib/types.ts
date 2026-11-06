/** Mirrors the .NET API contracts (enums are serialized as strings, dates as yyyy-MM-dd). */

export type InvoiceStatus =
  | "Received" | "Preprocessing" | "Extracting" | "Extracted" | "Validating"
  | "NeedsReview" | "PendingApproval" | "Approved" | "Posting" | "Posted"
  | "PostingFailed" | "Rejected" | "Duplicate" | "Closed";

export const PROCESSING: InvoiceStatus[] = ["Received", "Preprocessing", "Extracting", "Extracted", "Validating", "Approved", "Posting"];

export interface InvoiceLine {
  sku: string | null;
  description: string | null;
  quantity: number | null;
  unitPrice: number | null;
  lineTotal: number | null;
  vatRate: number | null;
}

export interface Invoice {
  supplierName: string | null;
  supplierTrn: string | null;
  customerTrn: string | null;
  invoiceNumber: string | null;
  invoiceDate: string | null;
  dueDate: string | null;
  poNumber: string | null;
  currency: string | null;
  lines: InvoiceLine[];
  subtotal: number | null;
  vatTotal: number | null;
  total: number | null;
  iban: string | null;
}

export interface FieldEvidence { field: string; page: number | null; sourceText: string }

export interface ExtractionResult {
  status: "Succeeded" | "NotAnInvoice" | "Failed";
  documentType: string;
  evidence: FieldEvidence[];
  issues: string[];
  error: string | null;
  usage: { extractor: string; model: string | null; promptVersion: string | null; pages: number; costUsd: number; duration: string };
}

export type Severity = "Info" | "Warning" | "Error";

export interface Finding {
  ruleCode: string;
  severity: Severity;
  field: string | null;
  expected: string | null;
  actual: string | null;
  messageEn: string;
  messageAr: string;
}

export interface PoLine {
  lineNo: number;
  sku: string | null;
  description: string;
  quantity: number;
  unitPrice: number;
  quantityInvoiced: number;
  quantityReceived: number;
}

export interface LineMatch {
  invoiceLineIndex: number;
  poLine: PoLine | null;
  method: "None" | "Sku" | "Description";
  score: number;
  suggestions: PoLine[];
}

export interface ValidationReport {
  outcome: "Approved" | "PendingApproval" | "NeedsReview" | "Duplicate";
  findings: Finding[];
  supplierId: string | null;
  supplierMatchMethod: "None" | "Trn" | "Name";
  poNumber: string | null;
  lineMatches: LineMatch[];
  ruleset: string;
}

export interface DocumentState {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  submittedBy: string;
  receivedAt: string;
  status: InvoiceStatus;
  statusChangedAt: string;
  supplierId: string | null;
  supplierName: string | null;
  invoiceNumber: string | null;
  currency: string | null;
  total: number | null;
  resolvedBy: string | null;
  approvedBy: string | null;
  erpReference: string | null;
}

export interface Correction { field: string; oldValue: string | null; newValue: string | null; userId: string; at: string }

export interface AuditEvent {
  at: string;
  actor: string;
  action: string;
  fromStatus: InvoiceStatus | null;
  toStatus: InvoiceStatus | null;
  details: string | null;
}

export interface InvoiceDetails {
  document: DocumentState;
  invoice: Invoice | null;
  extraction: ExtractionResult | null;
  validation: ValidationReport | null;
  corrections: Correction[];
  audit: AuditEvent[];
  /** Set for duplicates: the earlier document this one repeats. */
  duplicateOf: { id: string; fileName: string; receivedAt: string; status: InvoiceStatus; reason: "file" | "number" } | null;
  /** User id → display name, for everyone mentioned on this invoice. */
  people: Record<string, string> | null;
}

export interface InvoiceSummary {
  id: string;
  fileName: string;
  status: InvoiceStatus;
  receivedAt: string;
  statusChangedAt: string;
  submittedBy: string;
  supplierName: string | null;
  invoiceNumber: string | null;
  invoiceDate: string | null;
  currency: string | null;
  total: number | null;
  errorCodes: string[];
  submittedByName: string | null;
}

export interface Me {
  id: string;
  email: string | null;
  name: string;
  roles: string[];
  permissions: string[];
  approvalThreshold: number;
  approvalLimit: number | null;
  approvalUnlimited: boolean;
  mustChangePassword: boolean;
  language: string;
  mfaEnabled: boolean;
  /** Admin/approver without two-step sign-in: limited to the setup page until it is on. */
  mfaSetupRequired: boolean;
}

export const emptyInvoice = (): Invoice => ({
  supplierName: null, supplierTrn: null, customerTrn: null, invoiceNumber: null, invoiceDate: null, dueDate: null,
  poNumber: null, currency: null, lines: [], subtotal: null, vatTotal: null, total: null, iban: null,
});
