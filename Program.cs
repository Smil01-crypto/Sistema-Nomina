using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NominaApp
{
    #region Models
    public class Empleado
    {
        public int Id { get; set; } // Código
        public required string Nombre { get; set; }
        public required string Departamento { get; set; }
        public decimal SalarioBase { get; set; }
    }

    public class NominaLinea
    {
        public required Empleado Empleado { get; set; }
        public decimal SalarioBruto { get; set; }
        public decimal AFP { get; set; }
        public decimal ARS { get; set; }
        public decimal ISR { get; set; }
        public decimal TotalDeducciones => AFP + ARS + ISR;
        public decimal SalarioNeto => SalarioBruto - TotalDeducciones;
    }
    #endregion

    #region Repositorios (Interfaz + Implementacion SQLite)
    public interface IEmpleadoRepository
    {
        void EnsureDatabase();
        int Add(Empleado e);
        Empleado? Get(int id);
        List<Empleado> GetAll();
        void Update(Empleado e);
        void Delete(int id);
    }

    public class SqliteEmpleadoRepository : IEmpleadoRepository
    {
        private readonly string _connectionString;
        public SqliteEmpleadoRepository(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
            EnsureDatabase();
        }

        public void EnsureDatabase()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Tabla Empleados
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Empleados (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Nombre TEXT NOT NULL,
                    Departamento TEXT,
                    SalarioBase REAL NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();

            // Tabla Nominas (almacenar pagos si se desea)
            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = @"
                CREATE TABLE IF NOT EXISTS Nominas (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    EmpleadoId INTEGER,
                    Fecha TEXT,
                    SalarioBruto REAL,
                    AFP REAL,
                    ARS REAL,
                    ISR REAL,
                    SalarioNeto REAL,
                    FOREIGN KEY(EmpleadoId) REFERENCES Empleados(Id)
                );
            ";
            cmd2.ExecuteNonQuery();
        }

        public int Add(Empleado e)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Empleados (Nombre, Departamento, SalarioBase) VALUES (@n, @d, @s); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@n", e.Nombre);
            cmd.Parameters.AddWithValue("@d", e.Departamento ?? "");
            cmd.Parameters.AddWithValue("@s", e.SalarioBase);
            var result = cmd.ExecuteScalar();
            long id = result is DBNull ? 0 : Convert.ToInt64(result);
            return (int)id;
        }

        public Empleado? Get(int id)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Nombre, Departamento, SalarioBase FROM Empleados WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                return new Empleado
                {
                    Id = rdr.GetInt32(0),
                    Nombre = rdr.GetString(1),
                    Departamento = rdr.GetString(2),
                    SalarioBase = rdr.GetDecimal(3)
                };
            }
            return null;
        }

        public List<Empleado> GetAll()
        {
            var list = new List<Empleado>();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Nombre, Departamento, SalarioBase FROM Empleados ORDER BY Id";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                list.Add(new Empleado
                {
                    Id = rdr.GetInt32(0),
                    Nombre = rdr.GetString(1),
                    Departamento = rdr.GetString(2),
                    SalarioBase = rdr.GetDecimal(3)
                });
            }
            return list;
        }

        public void Update(Empleado e)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Empleados SET Nombre=@n, Departamento=@d, SalarioBase=@s WHERE Id=@id";
            cmd.Parameters.AddWithValue("@n", e.Nombre);
            cmd.Parameters.AddWithValue("@d", e.Departamento ?? "");
            cmd.Parameters.AddWithValue("@s", e.SalarioBase);
            cmd.Parameters.AddWithValue("@id", e.Id);
            cmd.ExecuteNonQuery();
        }

        public void Delete(int id)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Empleados WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }
    #endregion

    #region Servicio de Nomina
    public class NominaService
    {
        // Tasas (fijas según enunciado)
        private const decimal TasaAFP = 0.0287m; // 2.87%
        private const decimal TasaARS = 0.0304m; // 3.04%
        // ISR es opcional: aquí usamos una regla simple (ejemplo didáctico)
        // Puedes reemplazar por la tabla real de impuesto si la tienes.
        // Para la tarea dejaré un ISR progresivo muy simplificado:
        //  - SalarioBruto <= 20000 => ISR 0%
        //  - 20001 - 40000 => 5%
        //  - >40000 => 10%
        public decimal CalcularAFP(decimal salarioBruto) => Decimal.Round(salarioBruto * TasaAFP, 2, MidpointRounding.AwayFromZero);
        public decimal CalcularARS(decimal salarioBruto) => Decimal.Round(salarioBruto * TasaARS, 2, MidpointRounding.AwayFromZero);
        public decimal CalcularISR(decimal salarioBruto)
        {
            decimal tasa = 0m;
            if (salarioBruto <= 20000m) tasa = 0m;
            else if (salarioBruto <= 40000m) tasa = 0.05m;
            else tasa = 0.10m;
            return Decimal.Round(salarioBruto * tasa, 2, MidpointRounding.AwayFromZero);
        }

        public NominaLinea CalcularLinea(Empleado e)
        {
            var bruto = e.SalarioBase;
            var afp = CalcularAFP(bruto);
            var ars = CalcularARS(bruto);
            var isr = CalcularISR(bruto); // opcional, la incluimos
            return new NominaLinea
            {
                Empleado = e,
                SalarioBruto = bruto,
                AFP = afp,
                ARS = ars,
                ISR = isr
            };
        }

        // Genera la nómina para un listado de empleados (ej: mes)
        public List<NominaLinea> GenerarNomina(IEnumerable<Empleado> empleados)
        {
            var list = new List<NominaLinea>();
            foreach (var e in empleados)
                list.Add(CalcularLinea(e));
            return list;
        }

        // Exportar CSV simple
        public void ExportarCsv(List<NominaLinea> lineas, string rutaArchivo)
        {
            using var sw = new StreamWriter(rutaArchivo);
            sw.WriteLine("Empleado,SalarioBruto,AFP,ARS,ISR,TotalDeducciones,SalarioNeto");
            foreach (var l in lineas)
            {
                sw.WriteLine($"\"{l.Empleado.Nombre}\",{l.SalarioBruto.ToString(CultureInfo.InvariantCulture)},{l.AFP.ToString(CultureInfo.InvariantCulture)},{l.ARS.ToString(CultureInfo.InvariantCulture)},{l.ISR.ToString(CultureInfo.InvariantCulture)},{l.TotalDeducciones.ToString(CultureInfo.InvariantCulture)},{l.SalarioNeto.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }
    #endregion

    class Program
    {
        static void Main(string[] args)
        {
            string dbPath = "payroll.db";
            var repo = new SqliteEmpleadoRepository(dbPath);
            var nominaService = new NominaService();

            bool exit = false;
            while (!exit)
            {
                Console.WriteLine("\n=== Sistema de Nómina - Servicios Corporativos Caribe SRL ===");
                Console.WriteLine("1. Agregar empleado");
                Console.WriteLine("2. Consultar empleado por Id");
                Console.WriteLine("3. Listar empleados");
                Console.WriteLine("4. Editar empleado");
                Console.WriteLine("5. Eliminar empleado");
                Console.WriteLine("6. Generar nómina mensual (mostrar en pantalla)");
                Console.WriteLine("7. Exportar nómina a CSV");
                Console.WriteLine("8. Salir");
                Console.Write("Elija una opción: ");
                var opt = Console.ReadLine();

                try
                {
                    switch (opt)
                    {
                        case "1":
                            AgregarEmpleado(repo);
                            break;
                        case "2":
                            ConsultarEmpleado(repo);
                            break;
                        case "3":
                            ListarEmpleados(repo);
                            break;
                        case "4":
                            EditarEmpleado(repo);
                            break;
                        case "5":
                            EliminarEmpleado(repo);
                            break;
                        case "6":
                            GenerarNominaPantalla(repo, nominaService);
                            break;
                        case "7":
                            ExportarNomina(repo, nominaService);
                            break;
                        case "8":
                            exit = true;
                            break;
                        default:
                            Console.WriteLine("Opción inválida.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }

            Console.WriteLine("Fin del programa. Presione Enter para cerrar.");
            Console.ReadLine();
        }

        #region UI Helpers (CRUD)
        static void AgregarEmpleado(IEmpleadoRepository repo)
        {
            Console.WriteLine("\n--- Agregar empleado ---");
            Console.Write("Nombre: ");
            var nombre = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(nombre))
            {
                Console.WriteLine("Nombre no puede estar vacío.");
                return;
            }
            Console.Write("Departamento: ");
            var dep = Console.ReadLine()?.Trim();

            Console.Write("Salario base (numero): ");
            if (!Decimal.TryParse(Console.ReadLine(), out decimal salario))
            {
                Console.WriteLine("Salario inválido.");
                return;
            }
            if (salario < 0)
            {
                Console.WriteLine("Salario no puede ser negativo.");
                return;
            }

            // Validación duplicados por nombre y salario (ejemplo simple)
            var all = repo.GetAll();
            if (all.Any(x => x.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase) && x.SalarioBase == salario))
            {
                Console.WriteLine("Empleado duplicado (mismo nombre y salario).");
                return;
            }

            var e = new Empleado { Nombre = nombre, Departamento = dep ?? "", SalarioBase = salario };
            int id = repo.Add(e);
            Console.WriteLine($"Empleado agregado con Id {id}.");
        }

        static void ConsultarEmpleado(IEmpleadoRepository repo)
        {
            Console.Write("Id empleado: ");
            if (!int.TryParse(Console.ReadLine(), out int id))
            {
                Console.WriteLine("Id inválido.");
                return;
            }
            var e = repo.Get(id);
            if (e == null) Console.WriteLine("Empleado no encontrado.");
            else
            {
                Console.WriteLine($"Id: {e.Id} | Nombre: {e.Nombre} | Departamento: {e.Departamento} | Salario: {e.SalarioBase}");
            }
        }

        static void ListarEmpleados(IEmpleadoRepository repo)
        {
            var list = repo.GetAll();
            Console.WriteLine("\n--- Empleados ---");
            if (!list.Any()) { Console.WriteLine("No hay empleados registrados."); return; }
            foreach (var e in list) Console.WriteLine($"Id: {e.Id} | Nombre: {e.Nombre} | Departamento: {e.Departamento} | Salario: {e.SalarioBase}");
        }

        static void EditarEmpleado(IEmpleadoRepository repo)
        {
            Console.Write("Id a editar: ");
            if (!int.TryParse(Console.ReadLine(), out int id)) { Console.WriteLine("Id inválido."); return; }
            var e = repo.Get(id);
            if (e == null) { Console.WriteLine("Empleado no encontrado."); return; }
            Console.Write($"Nombre ({e.Nombre}): ");
            var newName = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(newName)) e.Nombre = newName;
            Console.Write($"Departamento ({e.Departamento}): ");
            var newDep = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(newDep)) e.Departamento = newDep;
            Console.Write($"Salario ({e.SalarioBase}): ");
            var s = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(s))
            {
                if (Decimal.TryParse(s, out decimal newSal))
                {
                    if (newSal < 0) { Console.WriteLine("Salario no puede ser negativo."); return; }
                    e.SalarioBase = newSal;
                }
                else { Console.WriteLine("Salario inválido."); return; }
            }
            repo.Update(e);
            Console.WriteLine("Empleado actualizado.");
        }

        static void EliminarEmpleado(IEmpleadoRepository repo)
        {
            Console.Write("Id a eliminar: ");
            if (!int.TryParse(Console.ReadLine(), out int id)) { Console.WriteLine("Id inválido."); return; }
            var e = repo.Get(id);
            if (e == null) { Console.WriteLine("Empleado no encontrado."); return; }
            Console.Write($"Confirmar borrar '{e.Nombre}'? (s/n): ");
            var c = Console.ReadLine();
            if (c?.ToLower() == "s")
            {
                repo.Delete(id);
                Console.WriteLine("Empleado eliminado.");
            }
            else Console.WriteLine("Operación cancelada.");
        }
        #endregion

        #region Nomina UI
        static void GenerarNominaPantalla(IEmpleadoRepository repo, NominaService nominaService)
        {
            var empleados = repo.GetAll();
            var nomina = nominaService.GenerarNomina(empleados);
            if (!nomina.Any()) { Console.WriteLine("No hay empleados para generar nómina."); return; }

            Console.WriteLine("\n--- Nómina Mensual ---");
            Console.WriteLine("Empleado | Bruto | AFP | ARS | ISR | Deducciones | Neto");
            decimal totalEmpresa = 0m;
            foreach (var l in nomina)
            {
                Console.WriteLine($"{l.Empleado.Nombre} | {l.SalarioBruto} | {l.AFP} | {l.ARS} | {l.ISR} | {l.TotalDeducciones} | {l.SalarioNeto}");
                totalEmpresa += l.SalarioNeto;
            }
            Console.WriteLine($"Total pagado por la empresa (suma netos): {totalEmpresa}");
        }

        static void ExportarNomina(IEmpleadoRepository repo, NominaService nominaService)
        {
            var empleados = repo.GetAll();
            var nomina = nominaService.GenerarNomina(empleados);
            if (!nomina.Any()) { Console.WriteLine("No hay empleados para exportar."); return; }

            string ruta = "nomina_export.csv";
            nominaService.ExportarCsv(nomina, ruta);
            Console.WriteLine($"Archivo exportado: {ruta} (en la carpeta del proyecto)");
        }
        #endregion
    }
}
