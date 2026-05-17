import { useEffect, useMemo, useState } from "react";
import {
  Alert,
  Anchor,
  AppShell,
  Badge,
  Card,
  Code,
  Container,
  Grid,
  Group,
  Loader,
  Select,
  SimpleGrid,
  Stack,
  Table,
  Text,
  TextInput,
  Title
} from "@mantine/core";
import { IconAlertCircle, IconBolt, IconChartHistogram, IconDatabase } from "@tabler/icons-react";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ReferenceLine,
  ReferenceArea,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis
} from "recharts";

type TariffMap = Record<string, string[]>;
type HourlyResponse = Record<string, number | null>;

type HourRow = {
  key: string;
  hour: number;
  hourLabel: string;
  price: number | null;
  dayIndex: 0 | 1;
  dayLabel: string;
  level: "low" | "mid" | "high" | "null";
  renderValue: number;
};

type Summary = {
  min: number | null;
  avg: number | null;
  max: number | null;
  count: number;
};

const distributors = [
  { value: "", label: "TGE bez dystrybucji" },
  { value: "tauron", label: "Tauron" },
  { value: "energa", label: "Energa" },
  { value: "pge", label: "PGE" },
  { value: "enea", label: "Enea" },
  { value: "stoen", label: "Stoen" }
];

const sellers = [
  { value: "", label: "Bez marży" },
  { value: "pstryk", label: "Pstryk" }
];

const units = [
  { value: "kwh", label: "zł/kWh" },
  { value: "mwh", label: "zł/MWh" }
];

const levelColors: Record<HourRow["level"], string> = {
  low: "#93c5fd",
  mid: "#3b82f6",
  high: "#f59e0b",
  null: "#6b7280"
};

function getTodayLocal(): string {
  const now = new Date();
  const offset = now.getTimezoneOffset();
  return new Date(now.getTime() - offset * 60000).toISOString().slice(0, 10);
}

function addDays(dateText: string, days: number): string {
  const [year, month, day] = dateText.split("-").map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

function formatDayLabel(dateText: string): string {
  return new Intl.DateTimeFormat("pl-PL", {
    weekday: "short",
    day: "2-digit",
    month: "2-digit"
  }).format(new Date(`${dateText}T00:00:00`));
}

function formatPrice(value: number | null): string {
  return value === null ? "-" : value.toFixed(2);
}

function buildUrl(date: string, distributor: string, tariff: string, seller: string, unit: string): string {
  const params = new URLSearchParams();
  params.set("date", date);
  if (distributor) {
    params.set("distributor", distributor);
  }
  if (tariff) {
    params.set("tariff", tariff);
  }
  if (seller) {
    params.set("seller", seller);
  }
  if (unit) {
    params.set("unit", unit);
  }
  return `/loxone/prices?${params.toString()}`;
}

function valuesOf(data: HourlyResponse): number[] {
  return Object.values(data).filter((value): value is number => value !== null);
}

function emptyHourlyResponse(): HourlyResponse {
  return Object.fromEntries(Array.from({ length: 24 }, (_, index) => [`h${index}`, null]));
}

function getLevel(value: number | null, min: number, max: number): HourRow["level"] {
  if (value === null) {
    return "null";
  }
  const span = max - min;
  if (span <= 0) {
    return "mid";
  }
  const position = (value - min) / span;
  if (position <= 0.33) {
    return "low";
  }
  if (position >= 0.67) {
    return "high";
  }
  return "mid";
}

function createRows(
  firstDate: string,
  firstData: HourlyResponse,
  secondDate: string,
  secondData: HourlyResponse
): HourRow[] {
  const actualValues = [...valuesOf(firstData), ...valuesOf(secondData)];
  const min = actualValues.length > 0 ? Math.min(...actualValues) : 0;
  const max = actualValues.length > 0 ? Math.max(...actualValues) : 1;
  const placeholderValue = Math.max(0.25, max * 0.67);

  const buildDayRows = (date: string, data: HourlyResponse, dayIndex: 0 | 1): HourRow[] =>
    Object.entries(data).map(([key, price], index) => ({
      key,
      hour: index,
      hourLabel: `${String(index).padStart(2, "0")}:00`,
      price,
      dayIndex,
      dayLabel: formatDayLabel(date),
      level: getLevel(price, min, max),
      renderValue: price ?? placeholderValue
    }));

  return [...buildDayRows(firstDate, firstData, 0), ...buildDayRows(secondDate, secondData, 1)];
}

function summarize(rows: HourRow[]): Summary {
  const prices = rows
    .filter((row) => row.dayIndex === 0 && row.price !== null)
    .map((row) => row.price as number);

  if (prices.length === 0) {
    return { min: null, avg: null, max: null, count: 0 };
  }

  return {
    min: Math.min(...prices),
    avg: prices.reduce((sum, value) => sum + value, 0) / prices.length,
    max: Math.max(...prices),
    count: prices.length
  };
}

async function fetchJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(await response.text());
  }
  return (await response.json()) as T;
}

export default function App() {
  const [tariffs, setTariffs] = useState<TariffMap>({});
  const [date, setDate] = useState(getTodayLocal());
  const [distributor, setDistributor] = useState("tauron");
  const [tariff, setTariff] = useState("g12w");
  const [seller, setSeller] = useState("pstryk");
  const [unit, setUnit] = useState("kwh");
  const [rows, setRows] = useState<HourRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchJson<TariffMap>("/loxone/tariffs")
      .then((data) => setTariffs(data))
      .catch((fetchError) => setError(fetchError instanceof Error ? fetchError.message : "Nie udało się pobrać taryf."));
  }, []);

  useEffect(() => {
    if (distributor && tariffs[distributor] && !tariffs[distributor].includes(tariff)) {
      setTariff(tariffs[distributor][0] ?? "");
    }
    if (!distributor && tariff) {
      setTariff("");
    }
  }, [distributor, tariff, tariffs]);

  useEffect(() => {
    let cancelled = false;
    const nextDate = addDays(date, 1);

    async function load(): Promise<void> {
      if (distributor && !tariff) {
        setRows([]);
        setLoading(false);
        setError("Wybierz taryfę dla wskazanego dystrybutora.");
        return;
      }

      setLoading(true);
      setError(null);
      const firstRequest = fetchJson<HourlyResponse>(buildUrl(date, distributor, tariff, seller, unit));
      const secondRequest = fetchJson<HourlyResponse>(buildUrl(nextDate, distributor, tariff, seller, unit)).catch(() =>
        emptyHourlyResponse()
      );

      try {
        const firstData = await firstRequest;
        if (!cancelled) {
          setRows(createRows(date, firstData, nextDate, emptyHourlyResponse()));
          setLoading(false);
        }

        const secondData = await secondRequest;
        if (!cancelled) {
          setRows(createRows(date, firstData, nextDate, secondData));
        }
      } catch (fetchError) {
        if (!cancelled) {
          setError(fetchError instanceof Error ? fetchError.message : "Nie udało się pobrać danych.");
          setRows([]);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    void load();
    return () => {
      cancelled = true;
    };
  }, [date, distributor, tariff, seller, unit]);

  const summary = useMemo(() => summarize(rows), [rows]);
  const tableRows = useMemo(() => rows.filter((row) => row.dayIndex === 0), [rows]);
  const dayTwoLabel = rows.find((row) => row.dayIndex === 1)?.dayLabel ?? formatDayLabel(addDays(date, 1));
  const unitLabel = unit === "mwh" ? "zł/MWh" : "zł/kWh";
  const currentUrl = buildUrl(date, distributor, tariff, seller, unit);
  const nextUrl = buildUrl(addDays(date, 1), distributor, tariff, seller, unit);

  return (
    <AppShell padding="md" header={{ height: { base: 118, sm: 96 } }} className="app-shell">
      <AppShell.Header className="app-header">
        <Container size="xl" className="app-header-inner">
          <Group justify="space-between" align="center" gap="md" wrap="wrap">
            <Group gap="sm">
              <div className="brand-icon">
                <IconBolt size={18} stroke={2.2} />
              </div>
              <div>
                <Title order={2} className="brand-title">
                  Ceny energii RDN
                </Title>
                <Text c="dimmed" size="sm">
                  Dashboard Loxone z taryfami, dystrybucją i widokiem 48h
                </Text>
              </div>
            </Group>
            <Group gap="xs" className="header-links">
              <Anchor href="/loxone/docs" size="sm">
                Dokumentacja
              </Anchor>
              <Anchor href="/loxone/tariffs" size="sm">
                Taryfy JSON
              </Anchor>
              <Anchor href="/health" size="sm">
                Health
              </Anchor>
            </Group>
          </Group>
        </Container>
      </AppShell.Header>

      <AppShell.Main className="app-main">
        <Container size="xl">
          <Stack gap="lg">
            <Card radius="md" className="filter-card">
              <Grid gutter="md" align="end">
                <Grid.Col span={{ base: 12, sm: 6, lg: 2 }}>
                  <TextInput
                    type="date"
                    label="Data"
                    value={date}
                    onChange={(event) => setDate(event.currentTarget.value)}
                  />
                </Grid.Col>
                <Grid.Col span={{ base: 12, sm: 6, lg: 2.5 }}>
                  <Select
                    label="Dystrybutor"
                    data={distributors}
                    value={distributor}
                    onChange={(value) => setDistributor(value ?? "")}
                    allowDeselect={false}
                  />
                </Grid.Col>
                <Grid.Col span={{ base: 12, sm: 6, lg: 2.5 }}>
                  <Select
                    label="Taryfa"
                    required={Boolean(distributor)}
                    data={(tariffs[distributor] ?? []).map((value) => ({ value, label: value.toUpperCase() }))}
                    value={tariff}
                    onChange={(value) => setTariff(value ?? "")}
                    disabled={!distributor}
                    searchable
                  />
                </Grid.Col>
                <Grid.Col span={{ base: 12, sm: 6, lg: 2.5 }}>
                  <Select
                    label="Sprzedawca"
                    data={sellers}
                    value={seller}
                    onChange={(value) => setSeller(value ?? "")}
                    allowDeselect={false}
                  />
                </Grid.Col>
                <Grid.Col span={{ base: 12, sm: 6, lg: 2.5 }}>
                  <Select
                    label="Jednostka"
                    data={units}
                    value={unit}
                    onChange={(value) => setUnit(value ?? "kwh")}
                    allowDeselect={false}
                  />
                </Grid.Col>
              </Grid>
              <Group justify="space-between" mt="md" gap="xs">
                <Text size="sm" c="dimmed">
                  Zmiana filtra od razu przelicza wykres, tabele i linki API.
                </Text>
                {loading && (
                  <Group gap="xs">
                    <Loader size="xs" />
                    <Text size="sm" c="dimmed">
                      Aktualizuję dane
                    </Text>
                  </Group>
                )}
              </Group>
            </Card>

            {error && (
              <Alert color="red" icon={<IconAlertCircle size={18} />} radius="md">
                {error}
              </Alert>
            )}

            <Card radius="xl" className="chart-card" padding="lg">
              <Group justify="space-between" mb="md">
                <Group gap="sm">
                  <div className="chart-icon">
                    <IconChartHistogram size={18} />
                  </div>
                  <div>
                    <Text className="chart-kicker">Wykres godzinowy 48h</Text>
                    <Text c="dimmed" size="sm">
                      {formatDayLabel(date)} i {dayTwoLabel}
                    </Text>
                  </div>
                </Group>
                {loading ? <Loader size="sm" color="gray" /> : <Badge color="gray">{unitLabel}</Badge>}
              </Group>

              <div className="chart-wrap">
                <div className="day-band day-band-left">{formatDayLabel(date)}</div>
                <div className="day-band day-band-right">{dayTwoLabel}</div>
                <ResponsiveContainer width="100%" height={320}>
                  <BarChart data={rows} margin={{ top: 28, right: 12, left: -18, bottom: 14 }} barCategoryGap={2}>
                    <ReferenceArea x1={-0.5} x2={23.5} fill="rgba(72, 121, 199, 0.12)" />
                    <ReferenceArea x1={23.5} x2={47.5} fill="rgba(173, 123, 64, 0.12)" />
                    <CartesianGrid stroke="rgba(255,255,255,0.07)" vertical={false} />
                    <XAxis
                      dataKey="hourLabel"
                      interval={1}
                      tick={{ fill: "#9aa5b5", fontSize: 11 }}
                      tickMargin={10}
                      axisLine={false}
                      tickLine={false}
                    />
                    <YAxis
                      tick={{ fill: "#9aa5b5", fontSize: 11 }}
                      tickFormatter={(value: number) => value.toFixed(2)}
                      axisLine={false}
                      tickLine={false}
                      width={44}
                    />
                    <Tooltip
                      cursor={{ fill: "rgba(255,255,255,0.04)" }}
                      content={({ active, payload }) => {
                        if (!active || !payload?.length) {
                          return null;
                        }

                        const point = payload[0].payload as HourRow;
                        return (
                          <div className="chart-tooltip">
                            <Text size="sm" fw={800} c="white">
                              {point.dayLabel} {point.hourLabel}
                            </Text>
                            <Text size="sm" c={point.price === null ? "gray.4" : "white"}>
                              {point.price === null ? "brak ceny" : `${point.price.toFixed(2)} ${unitLabel}`}
                            </Text>
                          </div>
                        );
                      }}
                    />
                    <ReferenceLine x={23.5} stroke="rgba(255,255,255,0.45)" strokeDasharray="5 5" />
                    <Bar dataKey="renderValue" radius={[2, 2, 0, 0]}>
                      {rows.map((row) => (
                        <Cell key={`${row.dayIndex}-${row.key}`} fill={levelColors[row.level]} />
                      ))}
                    </Bar>
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </Card>

            <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="md">
              <Card radius="md" className="metric-card">
                <Text className="metric-label">Minimum</Text>
                <Text className="metric-value">{formatPrice(summary.min)}</Text>
                <Text c="dimmed" size="sm">{unitLabel}</Text>
              </Card>
              <Card radius="md" className="metric-card">
                <Text className="metric-label">Średnia</Text>
                <Text className="metric-value">{formatPrice(summary.avg)}</Text>
                <Text c="dimmed" size="sm">{unitLabel}</Text>
              </Card>
              <Card radius="md" className="metric-card">
                <Text className="metric-label">Maksimum</Text>
                <Text className="metric-value">{formatPrice(summary.max)}</Text>
                <Text c="dimmed" size="sm">{unitLabel}</Text>
              </Card>
              <Card radius="md" className="metric-card">
                <Text className="metric-label">Dostepne godziny</Text>
                <Text className="metric-value">{summary.count}/24</Text>
                <Text c="dimmed" size="sm">dla wybranego dnia</Text>
              </Card>
            </SimpleGrid>

            <Card radius="md" className="table-card">
              <Group justify="space-between" mb="md">
                <div>
                  <Title order={4}>Tabela godzinowa</Title>
                  <Text c="dimmed" size="sm">
                    Dane dla {formatDayLabel(date)}
                  </Text>
                </div>
                <Badge color="teal" variant="light">
                  {distributor || "tge"} {tariff || "bez taryfy"} {seller || "bez marży"}
                </Badge>
              </Group>
              <Table.ScrollContainer minWidth={760}>
                <Table highlightOnHover verticalSpacing="sm">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Godzina</Table.Th>
                      <Table.Th>Klucz</Table.Th>
                      <Table.Th>Cena</Table.Th>
                      <Table.Th>Poziom</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {tableRows.map((row) => (
                      <Table.Tr key={`${row.dayIndex}-${row.key}`}>
                        <Table.Td>{row.hourLabel}</Table.Td>
                        <Table.Td>
                          <Code>{row.key}</Code>
                        </Table.Td>
                        <Table.Td>{row.price === null ? "brak" : `${row.price.toFixed(2)} ${unitLabel}`}</Table.Td>
                        <Table.Td>
                          <Badge
                            variant="light"
                            color={row.level === "low" ? "blue" : row.level === "high" ? "orange" : row.level === "null" ? "gray" : "indigo"}
                          >
                            {row.level === "low" ? "taniej" : row.level === "high" ? "drożej" : row.level === "null" ? "brak" : "średnio"}
                          </Badge>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            </Card>

            <Card radius="md" className="api-card">
              <Group gap="xs" mb="sm">
                <IconDatabase size={18} />
                <Title order={5}>API pod spodem</Title>
              </Group>
              <Stack gap="xs">
                <Code block>{currentUrl}</Code>
                <Code block>{nextUrl}</Code>
              </Stack>
            </Card>
          </Stack>
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
