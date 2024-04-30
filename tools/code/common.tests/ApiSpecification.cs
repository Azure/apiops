using System;

namespace common.tests;

internal static class TestApiSpecification
{
    public static string GraphQl { get; } =
        // Retrieved from https://studio.apollographql.com/public/star-wars-swapi/variant/current/schema/sdl
        """"
        schema {
          query: Root
        }

        """A single film."""
        type Film implements Node {
          characterConnection(after: String, before: String, first: Int, last: Int): FilmCharactersConnection

          """The ISO 8601 date format of the time that this resource was created."""
          created: String

          """The name of the director of this film."""
          director: String

          """The ISO 8601 date format of the time that this resource was edited."""
          edited: String

          """The episode number of this film."""
          episodeID: Int

          """The ID of an object"""
          id: ID!

          """The opening paragraphs at the beginning of this film."""
          openingCrawl: String
          planetConnection(after: String, before: String, first: Int, last: Int): FilmPlanetsConnection

          """The name(s) of the producer(s) of this film."""
          producers: [String]

          """The ISO 8601 date format of film release at original creator country."""
          releaseDate: String
          speciesConnection(after: String, before: String, first: Int, last: Int): FilmSpeciesConnection
          starshipConnection(after: String, before: String, first: Int, last: Int): FilmStarshipsConnection

          """The title of this film."""
          title: String
          vehicleConnection(after: String, before: String, first: Int, last: Int): FilmVehiclesConnection
        }

        """A connection to a list of items."""
        type FilmCharactersConnection {
          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          characters: [Person]

          """A list of edges."""
          edges: [FilmCharactersEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type FilmCharactersEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Person
        }

        """A connection to a list of items."""
        type FilmPlanetsConnection {
          """A list of edges."""
          edges: [FilmPlanetsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          planets: [Planet]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type FilmPlanetsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Planet
        }

        """A connection to a list of items."""
        type FilmSpeciesConnection {
          """A list of edges."""
          edges: [FilmSpeciesEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          species: [Species]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type FilmSpeciesEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Species
        }

        """A connection to a list of items."""
        type FilmStarshipsConnection {
          """A list of edges."""
          edges: [FilmStarshipsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          starships: [Starship]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type FilmStarshipsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Starship
        }

        """A connection to a list of items."""
        type FilmVehiclesConnection {
          """A list of edges."""
          edges: [FilmVehiclesEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          vehicles: [Vehicle]
        }

        """An edge in a connection."""
        type FilmVehiclesEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Vehicle
        }

        """A connection to a list of items."""
        type FilmsConnection {
          """A list of edges."""
          edges: [FilmsEdge]

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          films: [Film]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type FilmsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Film
        }

        """An object with an ID"""
        interface Node {
          """The id of the object."""
          id: ID!
        }

        """Information about pagination in a connection."""
        type PageInfo {
          """When paginating forwards, the cursor to continue."""
          endCursor: String

          """When paginating forwards, are there more items?"""
          hasNextPage: Boolean!

          """When paginating backwards, are there more items?"""
          hasPreviousPage: Boolean!

          """When paginating backwards, the cursor to continue."""
          startCursor: String
        }

        """A connection to a list of items."""
        type PeopleConnection {
          """A list of edges."""
          edges: [PeopleEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          people: [Person]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type PeopleEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Person
        }

        """An individual person or character within the Star Wars universe."""
        type Person implements Node {
          """
          The birth year of the person, using the in-universe standard of BBY or ABY -
          Before the Battle of Yavin or After the Battle of Yavin. The Battle of Yavin is
          a battle that occurs at the end of Star Wars episode IV: A New Hope.
          """
          birthYear: String

          """The ISO 8601 date format of the time that this resource was created."""
          created: String

          """The ISO 8601 date format of the time that this resource was edited."""
          edited: String

          """
          The eye color of this person. Will be "unknown" if not known or "n/a" if the
          person does not have an eye.
          """
          eyeColor: String
          filmConnection(after: String, before: String, first: Int, last: Int): PersonFilmsConnection

          """
          The gender of this person. Either "Male", "Female" or "unknown",
          "n/a" if the person does not have a gender.
          """
          gender: String

          """
          The hair color of this person. Will be "unknown" if not known or "n/a" if the
          person does not have hair.
          """
          hairColor: String

          """The height of the person in centimeters."""
          height: Int

          """A planet that this person was born on or inhabits."""
          homeworld: Planet

          """The ID of an object"""
          id: ID!

          """The mass of the person in kilograms."""
          mass: Float

          """The name of this person."""
          name: String

          """The skin color of this person."""
          skinColor: String

          """The species that this person belongs to, or null if unknown."""
          species: Species
          starshipConnection(after: String, before: String, first: Int, last: Int): PersonStarshipsConnection
          vehicleConnection(after: String, before: String, first: Int, last: Int): PersonVehiclesConnection
        }

        """A connection to a list of items."""
        type PersonFilmsConnection {
          """A list of edges."""
          edges: [PersonFilmsEdge]

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          films: [Film]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type PersonFilmsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Film
        }

        """A connection to a list of items."""
        type PersonStarshipsConnection {
          """A list of edges."""
          edges: [PersonStarshipsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          starships: [Starship]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type PersonStarshipsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Starship
        }

        """A connection to a list of items."""
        type PersonVehiclesConnection {
          """A list of edges."""
          edges: [PersonVehiclesEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          vehicles: [Vehicle]
        }

        """An edge in a connection."""
        type PersonVehiclesEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Vehicle
        }

        """
        A large mass, planet or planetoid in the Star Wars Universe, at the time of
        0 ABY.
        """
        type Planet implements Node {
          """The climates of this planet."""
          climates: [String]

          """The ISO 8601 date format of the time that this resource was created."""
          created: String

          """The diameter of this planet in kilometers."""
          diameter: Int

          """The ISO 8601 date format of the time that this resource was edited."""
          edited: String
          filmConnection(after: String, before: String, first: Int, last: Int): PlanetFilmsConnection

          """
          A number denoting the gravity of this planet, where "1" is normal or 1 standard
          G. "2" is twice or 2 standard Gs. "0.5" is half or 0.5 standard Gs.
          """
          gravity: String

          """The ID of an object"""
          id: ID!

          """The name of this planet."""
          name: String

          """
          The number of standard days it takes for this planet to complete a single orbit
          of its local star.
          """
          orbitalPeriod: Int

          """The average population of sentient beings inhabiting this planet."""
          population: Float
          residentConnection(after: String, before: String, first: Int, last: Int): PlanetResidentsConnection

          """
          The number of standard hours it takes for this planet to complete a single
          rotation on its axis.
          """
          rotationPeriod: Int

          """
          The percentage of the planet surface that is naturally occuring water or bodies
          of water.
          """
          surfaceWater: Float

          """The terrains of this planet."""
          terrains: [String]
        }

        """A connection to a list of items."""
        type PlanetFilmsConnection {
          """A list of edges."""
          edges: [PlanetFilmsEdge]

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          films: [Film]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type PlanetFilmsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Film
        }

        """A connection to a list of items."""
        type PlanetResidentsConnection {
          """A list of edges."""
          edges: [PlanetResidentsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          residents: [Person]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type PlanetResidentsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Person
        }

        """A connection to a list of items."""
        type PlanetsConnection {
          """A list of edges."""
          edges: [PlanetsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          planets: [Planet]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type PlanetsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Planet
        }

        type Root {
          allFilms(after: String, before: String, first: Int, last: Int): FilmsConnection
          allPeople(after: String, before: String, first: Int, last: Int): PeopleConnection
          allPlanets(after: String, before: String, first: Int, last: Int): PlanetsConnection
          allSpecies(after: String, before: String, first: Int, last: Int): SpeciesConnection
          allStarships(after: String, before: String, first: Int, last: Int): StarshipsConnection
          allVehicles(after: String, before: String, first: Int, last: Int): VehiclesConnection
          film(filmID: ID, id: ID): Film

          """Fetches an object given its ID"""
          node(
            """The ID of an object"""
            id: ID!
          ): Node
          person(id: ID, personID: ID): Person
          planet(id: ID, planetID: ID): Planet
          species(id: ID, speciesID: ID): Species
          starship(id: ID, starshipID: ID): Starship
          vehicle(id: ID, vehicleID: ID): Vehicle
        }

        """A type of person or character within the Star Wars Universe."""
        type Species implements Node {
          """The average height of this species in centimeters."""
          averageHeight: Float

          """The average lifespan of this species in years, null if unknown."""
          averageLifespan: Int

          """The classification of this species, such as "mammal" or "reptile"."""
          classification: String

          """The ISO 8601 date format of the time that this resource was created."""
          created: String

          """The designation of this species, such as "sentient"."""
          designation: String

          """The ISO 8601 date format of the time that this resource was edited."""
          edited: String

          """
          Common eye colors for this species, null if this species does not typically
          have eyes.
          """
          eyeColors: [String]
          filmConnection(after: String, before: String, first: Int, last: Int): SpeciesFilmsConnection

          """
          Common hair colors for this species, null if this species does not typically
          have hair.
          """
          hairColors: [String]

          """A planet that this species originates from."""
          homeworld: Planet

          """The ID of an object"""
          id: ID!

          """The language commonly spoken by this species."""
          language: String

          """The name of this species."""
          name: String
          personConnection(after: String, before: String, first: Int, last: Int): SpeciesPeopleConnection

          """
          Common skin colors for this species, null if this species does not typically
          have skin.
          """
          skinColors: [String]
        }

        """A connection to a list of items."""
        type SpeciesConnection {
          """A list of edges."""
          edges: [SpeciesEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          species: [Species]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type SpeciesEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Species
        }

        """A connection to a list of items."""
        type SpeciesFilmsConnection {
          """A list of edges."""
          edges: [SpeciesFilmsEdge]

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          films: [Film]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type SpeciesFilmsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Film
        }

        """A connection to a list of items."""
        type SpeciesPeopleConnection {
          """A list of edges."""
          edges: [SpeciesPeopleEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          people: [Person]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type SpeciesPeopleEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Person
        }

        """A single transport craft that has hyperdrive capability."""
        type Starship implements Node {
          """
          The Maximum number of Megalights this starship can travel in a standard hour.
          A "Megalight" is a standard unit of distance and has never been defined before
          within the Star Wars universe. This figure is only really useful for measuring
          the difference in speed of starships. We can assume it is similar to AU, the
          distance between our Sun (Sol) and Earth.
          """
          MGLT: Int

          """The maximum number of kilograms that this starship can transport."""
          cargoCapacity: Float

          """
          The maximum length of time that this starship can provide consumables for its
          entire crew without having to resupply.
          """
          consumables: String

          """The cost of this starship new, in galactic credits."""
          costInCredits: Float

          """The ISO 8601 date format of the time that this resource was created."""
          created: String

          """The number of personnel needed to run or pilot this starship."""
          crew: String

          """The ISO 8601 date format of the time that this resource was edited."""
          edited: String
          filmConnection(after: String, before: String, first: Int, last: Int): StarshipFilmsConnection

          """The class of this starships hyperdrive."""
          hyperdriveRating: Float

          """The ID of an object"""
          id: ID!

          """The length of this starship in meters."""
          length: Float

          """The manufacturers of this starship."""
          manufacturers: [String]

          """
          The maximum speed of this starship in atmosphere. null if this starship is
          incapable of atmosphering flight.
          """
          maxAtmospheringSpeed: Int

          """
          The model or official name of this starship. Such as "T-65 X-wing" or "DS-1
          Orbital Battle Station".
          """
          model: String

          """The name of this starship. The common name, such as "Death Star"."""
          name: String

          """The number of non-essential people this starship can transport."""
          passengers: String
          pilotConnection(after: String, before: String, first: Int, last: Int): StarshipPilotsConnection

          """
          The class of this starship, such as "Starfighter" or "Deep Space Mobile
          Battlestation"
          """
          starshipClass: String
        }

        """A connection to a list of items."""
        type StarshipFilmsConnection {
          """A list of edges."""
          edges: [StarshipFilmsEdge]

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          films: [Film]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type StarshipFilmsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Film
        }

        """A connection to a list of items."""
        type StarshipPilotsConnection {
          """A list of edges."""
          edges: [StarshipPilotsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          pilots: [Person]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type StarshipPilotsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Person
        }

        """A connection to a list of items."""
        type StarshipsConnection {
          """A list of edges."""
          edges: [StarshipsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          starships: [Starship]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type StarshipsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Starship
        }

        """A single transport craft that does not have hyperdrive capability"""
        type Vehicle implements Node {
          """The maximum number of kilograms that this vehicle can transport."""
          cargoCapacity: Float

          """
          The maximum length of time that this vehicle can provide consumables for its
          entire crew without having to resupply.
          """
          consumables: String

          """The cost of this vehicle new, in Galactic Credits."""
          costInCredits: Float

          """The ISO 8601 date format of the time that this resource was created."""
          created: String

          """The number of personnel needed to run or pilot this vehicle."""
          crew: String

          """The ISO 8601 date format of the time that this resource was edited."""
          edited: String
          filmConnection(after: String, before: String, first: Int, last: Int): VehicleFilmsConnection

          """The ID of an object"""
          id: ID!

          """The length of this vehicle in meters."""
          length: Float

          """The manufacturers of this vehicle."""
          manufacturers: [String]

          """The maximum speed of this vehicle in atmosphere."""
          maxAtmospheringSpeed: Int

          """
          The model or official name of this vehicle. Such as "All-Terrain Attack
          Transport".
          """
          model: String

          """
          The name of this vehicle. The common name, such as "Sand Crawler" or "Speeder
          bike".
          """
          name: String

          """The number of non-essential people this vehicle can transport."""
          passengers: String
          pilotConnection(after: String, before: String, first: Int, last: Int): VehiclePilotsConnection

          """The class of this vehicle, such as "Wheeled" or "Repulsorcraft"."""
          vehicleClass: String
        }

        """A connection to a list of items."""
        type VehicleFilmsConnection {
          """A list of edges."""
          edges: [VehicleFilmsEdge]

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          films: [Film]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type VehicleFilmsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Film
        }

        """A connection to a list of items."""
        type VehiclePilotsConnection {
          """A list of edges."""
          edges: [VehiclePilotsEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          pilots: [Person]

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int
        }

        """An edge in a connection."""
        type VehiclePilotsEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Person
        }

        """A connection to a list of items."""
        type VehiclesConnection {
          """A list of edges."""
          edges: [VehiclesEdge]

          """Information to aid in pagination."""
          pageInfo: PageInfo!

          """
          A count of the total number of objects in this connection, ignoring pagination.
          This allows a client to fetch the first five objects by passing "5" as the
          argument to "first", then fetch the total count so it could display "5 of 83",
          for example.
          """
          totalCount: Int

          """
          A list of all of the objects returned in the connection. This is a convenience
          field provided for quickly exploring the API; rather than querying for
          "{ edges { node } }" when no edge data is needed, this field can be be used
          instead. Note that when clients like Relay need to fetch the "cursor" field on
          the edge to enable efficient pagination, this shortcut cannot be used, and the
          full "{ edges { node } }" version should be used instead.
          """
          vehicles: [Vehicle]
        }

        """An edge in a connection."""
        type VehiclesEdge {
          """A cursor for use in pagination"""
          cursor: String!

          """The item at the end of the edge"""
          node: Vehicle
        }
        
        """";

    public static string GetWsdl(ApiName apiName, Uri serviceUrl) =>
    // Imported from http://www.dneonline.com/calculator.asmx?WSDL
    $"""
    <?xml version="1.0" encoding="utf-8"?>
    <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:tns="http://tempuri.org/"
        targetNamespace="http://tempuri.org/">
        <wsdl:types>
            <s:schema elementFormDefault="qualified" targetNamespace="http://tempuri.org/"
                xmlns:s="http://www.w3.org/2001/XMLSchema"
                xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                xmlns:tm="http://microsoft.com/wsdl/mime/textMatching/"
                xmlns:soapenc="http://schemas.xmlsoap.org/soap/encoding/"
                xmlns:mime="http://schemas.xmlsoap.org/wsdl/mime/" xmlns:tns="http://tempuri.org/"
                xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                xmlns:http="http://schemas.xmlsoap.org/wsdl/http/"
                xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/">
                <s:element name="Add">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="intA" type="s:int" />
                            <s:element minOccurs="1" maxOccurs="1" name="intB" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
                <s:element name="AddResponse">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="AddResult" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
                <s:element name="Subtract">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="intA" type="s:int" />
                            <s:element minOccurs="1" maxOccurs="1" name="intB" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
                <s:element name="SubtractResponse">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="SubtractResult" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
                <s:element name="Multiply">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="intA" type="s:int" />
                            <s:element minOccurs="1" maxOccurs="1" name="intB" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
                <s:element name="MultiplyResponse">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="MultiplyResult" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
                <s:element name="Divide">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="intA" type="s:int" />
                            <s:element minOccurs="1" maxOccurs="1" name="intB" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
                <s:element name="DivideResponse">
                    <s:complexType>
                        <s:sequence>
                            <s:element minOccurs="1" maxOccurs="1" name="DivideResult" type="s:int" />
                        </s:sequence>
                    </s:complexType>
                </s:element>
            </s:schema>
        </wsdl:types>
        <wsdl:message name="Add_InputMessage">
            <wsdl:part name="parameters" element="tns:Add" />
        </wsdl:message>
        <wsdl:message name="Add_OutputMessage">
            <wsdl:part name="parameters" element="tns:AddResponse" />
        </wsdl:message>
        <wsdl:message name="Subtract_InputMessage">
            <wsdl:part name="parameters" element="tns:Subtract" />
        </wsdl:message>
        <wsdl:message name="Subtract_OutputMessage">
            <wsdl:part name="parameters" element="tns:SubtractResponse" />
        </wsdl:message>
        <wsdl:message name="Multiply_InputMessage">
            <wsdl:part name="parameters" element="tns:Multiply" />
        </wsdl:message>
        <wsdl:message name="Multiply_OutputMessage">
            <wsdl:part name="parameters" element="tns:MultiplyResponse" />
        </wsdl:message>
        <wsdl:message name="Divide_InputMessage">
            <wsdl:part name="parameters" element="tns:Divide" />
        </wsdl:message>
        <wsdl:message name="Divide_OutputMessage">
            <wsdl:part name="parameters" element="tns:DivideResponse" />
        </wsdl:message>
        <wsdl:portType name="{apiName}">
            <wsdl:operation name="Add">
                <wsdl:input message="tns:Add_InputMessage" />
                <wsdl:output message="tns:Add_OutputMessage" />
            </wsdl:operation>
            <wsdl:operation name="Subtract">
                <wsdl:input message="tns:Subtract_InputMessage" />
                <wsdl:output message="tns:Subtract_OutputMessage" />
            </wsdl:operation>
            <wsdl:operation name="Multiply">
                <wsdl:input message="tns:Multiply_InputMessage" />
                <wsdl:output message="tns:Multiply_OutputMessage" />
            </wsdl:operation>
            <wsdl:operation name="Divide">
                <wsdl:input message="tns:Divide_InputMessage" />
                <wsdl:output message="tns:Divide_OutputMessage" />
            </wsdl:operation>
        </wsdl:portType>
        <wsdl:binding name="{apiName}" type="tns:{apiName}">
            <binding transport="http://schemas.xmlsoap.org/soap/http"
                xmlns="http://schemas.xmlsoap.org/wsdl/soap/" />
            <wsdl:operation name="Add">
                <soap:operation soapAction="http://tempuri.org/Add"
                    xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                <wsdl:input>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:input>
                <wsdl:output>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:output>
            </wsdl:operation>
            <wsdl:operation name="Subtract">
                <soap:operation soapAction="http://tempuri.org/Subtract"
                    xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                <wsdl:input>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:input>
                <wsdl:output>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:output>
            </wsdl:operation>
            <wsdl:operation name="Multiply">
                <soap:operation soapAction="http://tempuri.org/Multiply"
                    xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                <wsdl:input>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:input>
                <wsdl:output>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:output>
            </wsdl:operation>
            <wsdl:operation name="Divide">
                <soap:operation soapAction="http://tempuri.org/Divide"
                    xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                <wsdl:input>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:input>
                <wsdl:output>
                    <soap:body use="literal" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" />
                </wsdl:output>
            </wsdl:operation>
        </wsdl:binding>
        <wsdl:service name="{apiName}">
            <wsdl:port name="{apiName}" binding="tns:{apiName}">
                <address location="{serviceUrl}{apiName}"
                    xmlns="http://schemas.xmlsoap.org/wsdl/soap/" />
            </wsdl:port>
        </wsdl:service>
    </wsdl:definitions>
    """;

    public static string OpenApi { get; } =
    // Retrieved from https://petstore3.swagger.io/api/v3/openapi.json
    $$"""
    {
        "openapi": "3.0.2",
        "info": {
            "title": "Swagger Petstore - OpenAPI 3.0",
            "termsOfService": "http://swagger.io/terms/",
            "contact": {
                "email": "apiteam@swagger.io"
            },
            "license": {
                "name": "Apache 2.0",
                "url": "http://www.apache.org/licenses/LICENSE-2.0.html"
            },
            "version": "1.0.17"
        },
        "externalDocs": {
            "description": "Find out more about Swagger",
            "url": "http://swagger.io"
        },
        "servers": [
            {
                "url": "/api/v3"
            }
        ],
        "tags": [
        ],
        "paths": {
            "/pet": {
                "put": {
                    "summary": "Update an existing pet",
                    "description": "Update an existing pet by Id",
                    "operationId": "updatePet",
                    "requestBody": {
                        "description": "Update an existent pet in the store",
                        "content": {
                            "application/json": {
                                "schema": {
                                    "$ref": "#/components/schemas/Pet"
                                }
                            },
                            "application/xml": {
                                "schema": {
                                    "$ref": "#/components/schemas/Pet"
                                }
                            },
                            "application/x-www-form-urlencoded": {
                                "schema": {
                                    "$ref": "#/components/schemas/Pet"
                                }
                            }
                        },
                        "required": true
                    },
                    "responses": {
                        "200": {
                            "description": "Successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Pet"
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Pet"
                                    }
                                }
                            }
                        },
                        "400": {
                            "description": "Invalid ID supplied"
                        },
                        "404": {
                            "description": "Pet not found"
                        },
                        "405": {
                            "description": "Validation exception"
                        }
                    },
                    "security": [
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                },
                "post": {
                    "summary": "Add a new pet to the store",
                    "description": "Add a new pet to the store",
                    "operationId": "addPet",
                    "requestBody": {
                        "description": "Create a new pet in the store",
                        "content": {
                            "application/json": {
                                "schema": {
                                    "$ref": "#/components/schemas/Pet"
                                }
                            },
                            "application/xml": {
                                "schema": {
                                    "$ref": "#/components/schemas/Pet"
                                }
                            },
                            "application/x-www-form-urlencoded": {
                                "schema": {
                                    "$ref": "#/components/schemas/Pet"
                                }
                            }
                        },
                        "required": true
                    },
                    "responses": {
                        "200": {
                            "description": "Successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Pet"
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Pet"
                                    }
                                }
                            }
                        },
                        "405": {
                            "description": "Invalid input"
                        }
                    },
                    "security": [
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                }
            },
            "/pet/findByStatus": {
                "get": {
                    "summary": "Finds Pets by status",
                    "description": "Multiple status values can be provided with comma separated strings",
                    "operationId": "findPetsByStatus",
                    "parameters": [
                        {
                            "name": "status",
                            "in": "query",
                            "description": "Status values that need to be considered for filter",
                            "required": false,
                            "explode": true,
                            "schema": {
                                "type": "string",
                                "default": "available",
                                "enum": [
                                    "available",
                                    "pending",
                                    "sold"
                                ]
                            }
                        }
                    ],
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "type": "array",
                                        "items": {
                                            "$ref": "#/components/schemas/Pet"
                                        }
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "type": "array",
                                        "items": {
                                            "$ref": "#/components/schemas/Pet"
                                        }
                                    }
                                }
                            }
                        },
                        "400": {
                            "description": "Invalid status value"
                        }
                    },
                    "security": [
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                }
            },
            "/pet/findByTags": {
                "get": {
                    "summary": "Finds Pets by tags",
                    "description": "Multiple tags can be provided with comma separated strings. Use tag1, tag2, tag3 for testing.",
                    "operationId": "findPetsByTags",
                    "parameters": [
                        {
                            "name": "tags",
                            "in": "query",
                            "description": "Tags to filter by",
                            "required": false,
                            "explode": true,
                            "schema": {
                                "type": "array",
                                "items": {
                                    "type": "string"
                                }
                            }
                        }
                    ],
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "type": "array",
                                        "items": {
                                            "$ref": "#/components/schemas/Pet"
                                        }
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "type": "array",
                                        "items": {
                                            "$ref": "#/components/schemas/Pet"
                                        }
                                    }
                                }
                            }
                        },
                        "400": {
                            "description": "Invalid tag value"
                        }
                    },
                    "security": [
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                }
            },
            "/pet/{petId}": {
                "get": {
                    "summary": "Find pet by ID",
                    "description": "Returns a single pet",
                    "operationId": "getPetById",
                    "parameters": [
                        {
                            "name": "petId",
                            "in": "path",
                            "description": "ID of pet to return",
                            "required": true,
                            "schema": {
                                "type": "integer",
                                "format": "int64"
                            }
                        }
                    ],
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Pet"
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Pet"
                                    }
                                }
                            }
                        },
                        "400": {
                            "description": "Invalid ID supplied"
                        },
                        "404": {
                            "description": "Pet not found"
                        }
                    },
                    "security": [
                        {
                            "api_key": []
                        },
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                },
                "post": {
                    "summary": "Updates a pet in the store with form data",
                    "description": "",
                    "operationId": "updatePetWithForm",
                    "parameters": [
                        {
                            "name": "petId",
                            "in": "path",
                            "description": "ID of pet that needs to be updated",
                            "required": true,
                            "schema": {
                                "type": "integer",
                                "format": "int64"
                            }
                        },
                        {
                            "name": "name",
                            "in": "query",
                            "description": "Name of pet that needs to be updated",
                            "schema": {
                                "type": "string"
                            }
                        },
                        {
                            "name": "status",
                            "in": "query",
                            "description": "Status of pet that needs to be updated",
                            "schema": {
                                "type": "string"
                            }
                        }
                    ],
                    "responses": {
                        "405": {
                            "description": "Invalid input"
                        }
                    },
                    "security": [
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                },
                "delete": {
                    "summary": "Deletes a pet",
                    "description": "",
                    "operationId": "deletePet",
                    "parameters": [
                        {
                            "name": "api_key",
                            "in": "header",
                            "description": "",
                            "required": false,
                            "schema": {
                                "type": "string"
                            }
                        },
                        {
                            "name": "petId",
                            "in": "path",
                            "description": "Pet id to delete",
                            "required": true,
                            "schema": {
                                "type": "integer",
                                "format": "int64"
                            }
                        }
                    ],
                    "responses": {
                        "400": {
                            "description": "Invalid pet value"
                        }
                    },
                    "security": [
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                }
            },
            "/pet/{petId}/uploadImage": {
                "post": {
                    "summary": "uploads an image",
                    "description": "",
                    "operationId": "uploadFile",
                    "parameters": [
                        {
                            "name": "petId",
                            "in": "path",
                            "description": "ID of pet to update",
                            "required": true,
                            "schema": {
                                "type": "integer",
                                "format": "int64"
                            }
                        },
                        {
                            "name": "additionalMetadata",
                            "in": "query",
                            "description": "Additional Metadata",
                            "required": false,
                            "schema": {
                                "type": "string"
                            }
                        }
                    ],
                    "requestBody": {
                        "content": {
                            "application/octet-stream": {
                                "schema": {
                                    "type": "string",
                                    "format": "binary"
                                }
                            }
                        }
                    },
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/ApiResponse"
                                    }
                                }
                            }
                        }
                    },
                    "security": [
                        {
                            "petstore_auth": [
                                "write:pets",
                                "read:pets"
                            ]
                        }
                    ]
                }
            },
            "/store/inventory": {
                "get": {
                    "summary": "Returns pet inventories by status",
                    "description": "Returns a map of status codes to quantities",
                    "operationId": "getInventory",
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/json": {
                                    "schema": {
                                        "type": "object",
                                        "additionalProperties": {
                                            "type": "integer",
                                            "format": "int32"
                                        }
                                    }
                                }
                            }
                        }
                    },
                    "security": [
                        {
                            "api_key": []
                        }
                    ]
                }
            },
            "/store/order": {
                "post": {
                    "summary": "Place an order for a pet",
                    "description": "Place a new order in the store",
                    "operationId": "placeOrder",
                    "requestBody": {
                        "content": {
                            "application/json": {
                                "schema": {
                                    "$ref": "#/components/schemas/Order"
                                }
                            },
                            "application/xml": {
                                "schema": {
                                    "$ref": "#/components/schemas/Order"
                                }
                            },
                            "application/x-www-form-urlencoded": {
                                "schema": {
                                    "$ref": "#/components/schemas/Order"
                                }
                            }
                        }
                    },
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Order"
                                    }
                                }
                            }
                        },
                        "405": {
                            "description": "Invalid input"
                        }
                    }
                }
            },
            "/store/order/{orderId}": {
                "get": {
                    "summary": "Find purchase order by ID",
                    "description": "For valid response try integer IDs with value <= 5 or > 10. Other values will generate exceptions.",
                    "operationId": "getOrderById",
                    "parameters": [
                        {
                            "name": "orderId",
                            "in": "path",
                            "description": "ID of order that needs to be fetched",
                            "required": true,
                            "schema": {
                                "type": "integer",
                                "format": "int64"
                            }
                        }
                    ],
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Order"
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/Order"
                                    }
                                }
                            }
                        },
                        "400": {
                            "description": "Invalid ID supplied"
                        },
                        "404": {
                            "description": "Order not found"
                        }
                    }
                },
                "delete": {
                    "summary": "Delete purchase order by ID",
                    "description": "For valid response try integer IDs with value < 1000. Anything above 1000 or nonintegers will generate API errors",
                    "operationId": "deleteOrder",
                    "parameters": [
                        {
                            "name": "orderId",
                            "in": "path",
                            "description": "ID of the order that needs to be deleted",
                            "required": true,
                            "schema": {
                                "type": "integer",
                                "format": "int64"
                            }
                        }
                    ],
                    "responses": {
                        "400": {
                            "description": "Invalid ID supplied"
                        },
                        "404": {
                            "description": "Order not found"
                        }
                    }
                }
            },
            "/user": {
                "post": {
                    "summary": "Create user",
                    "description": "This can only be done by the logged in user.",
                    "operationId": "createUser",
                    "requestBody": {
                        "description": "Created user object",
                        "content": {
                            "application/json": {
                                "schema": {
                                    "$ref": "#/components/schemas/User"
                                }
                            },
                            "application/xml": {
                                "schema": {
                                    "$ref": "#/components/schemas/User"
                                }
                            },
                            "application/x-www-form-urlencoded": {
                                "schema": {
                                    "$ref": "#/components/schemas/User"
                                }
                            }
                        }
                    },
                    "responses": {
                        "default": {
                            "description": "successful operation",
                            "content": {
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/User"
                                    }
                                },
                                "application/xml": {
                                    "schema": {
                                        "$ref": "#/components/schemas/User"
                                    }
                                }
                            }
                        }
                    }
                }
            },
            "/user/createWithList": {
                "post": {
                    "summary": "Creates list of users with given input array",
                    "description": "Creates list of users with given input array",
                    "operationId": "createUsersWithListInput",
                    "requestBody": {
                        "content": {
                            "application/json": {
                                "schema": {
                                    "type": "array",
                                    "items": {
                                        "$ref": "#/components/schemas/User"
                                    }
                                }
                            }
                        }
                    },
                    "responses": {
                        "200": {
                            "description": "Successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "$ref": "#/components/schemas/User"
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/User"
                                    }
                                }
                            }
                        },
                        "default": {
                            "description": "successful operation"
                        }
                    }
                }
            },
            "/user/login": {
                "get": {
                    "summary": "Logs user into the system",
                    "description": "",
                    "operationId": "loginUser",
                    "parameters": [
                        {
                            "name": "username",
                            "in": "query",
                            "description": "The user name for login",
                            "required": false,
                            "schema": {
                                "type": "string"
                            }
                        },
                        {
                            "name": "password",
                            "in": "query",
                            "description": "The password for login in clear text",
                            "required": false,
                            "schema": {
                                "type": "string"
                            }
                        }
                    ],
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "headers": {
                                "X-Rate-Limit": {
                                    "description": "calls per hour allowed by the user",
                                    "schema": {
                                        "type": "integer",
                                        "format": "int32"
                                    }
                                },
                                "X-Expires-After": {
                                    "description": "date in UTC when token expires",
                                    "schema": {
                                        "type": "string",
                                        "format": "date-time"
                                    }
                                }
                            },
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "type": "string"
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "type": "string"
                                    }
                                }
                            }
                        },
                        "400": {
                            "description": "Invalid username/password supplied"
                        }
                    }
                }
            },
            "/user/logout": {
                "get": {
                    "summary": "Logs out current logged in user session",
                    "description": "",
                    "operationId": "logoutUser",
                    "parameters": [],
                    "responses": {
                        "default": {
                            "description": "successful operation"
                        }
                    }
                }
            },
            "/user/{username}": {
                "get": {
                    "summary": "Get user by user name",
                    "description": "",
                    "operationId": "getUserByName",
                    "parameters": [
                        {
                            "name": "username",
                            "in": "path",
                            "description": "The name that needs to be fetched. Use user1 for testing. ",
                            "required": true,
                            "schema": {
                                "type": "string"
                            }
                        }
                    ],
                    "responses": {
                        "200": {
                            "description": "successful operation",
                            "content": {
                                "application/xml": {
                                    "schema": {
                                        "$ref": "#/components/schemas/User"
                                    }
                                },
                                "application/json": {
                                    "schema": {
                                        "$ref": "#/components/schemas/User"
                                    }
                                }
                            }
                        },
                        "400": {
                            "description": "Invalid username supplied"
                        },
                        "404": {
                            "description": "User not found"
                        }
                    }
                },
                "put": {
                    "summary": "Update user",
                    "description": "This can only be done by the logged in user.",
                    "operationId": "updateUser",
                    "parameters": [
                        {
                            "name": "username",
                            "in": "path",
                            "description": "name that need to be deleted",
                            "required": true,
                            "schema": {
                                "type": "string"
                            }
                        }
                    ],
                    "requestBody": {
                        "description": "Update an existent user in the store",
                        "content": {
                            "application/json": {
                                "schema": {
                                    "$ref": "#/components/schemas/User"
                                }
                            },
                            "application/xml": {
                                "schema": {
                                    "$ref": "#/components/schemas/User"
                                }
                            },
                            "application/x-www-form-urlencoded": {
                                "schema": {
                                    "$ref": "#/components/schemas/User"
                                }
                            }
                        }
                    },
                    "responses": {
                        "default": {
                            "description": "successful operation"
                        }
                    }
                },
                "delete": {
                    "summary": "Delete user",
                    "description": "This can only be done by the logged in user.",
                    "operationId": "deleteUser",
                    "parameters": [
                        {
                            "name": "username",
                            "in": "path",
                            "description": "The name that needs to be deleted",
                            "required": true,
                            "schema": {
                                "type": "string"
                            }
                        }
                    ],
                    "responses": {
                        "400": {
                            "description": "Invalid username supplied"
                        },
                        "404": {
                            "description": "User not found"
                        }
                    }
                }
            }
        },
        "components": {
            "schemas": {
                "Order": {
                    "type": "object",
                    "properties": {
                        "id": {
                            "type": "integer",
                            "format": "int64",
                            "example": 10
                        },
                        "petId": {
                            "type": "integer",
                            "format": "int64",
                            "example": 198772
                        },
                        "quantity": {
                            "type": "integer",
                            "format": "int32",
                            "example": 7
                        },
                        "shipDate": {
                            "type": "string",
                            "format": "date-time"
                        },
                        "status": {
                            "type": "string",
                            "description": "Order Status",
                            "example": "approved",
                            "enum": [
                                "placed",
                                "approved",
                                "delivered"
                            ]
                        },
                        "complete": {
                            "type": "boolean"
                        }
                    },
                    "xml": {
                        "name": "order"
                    }
                },
                "Customer": {
                    "type": "object",
                    "properties": {
                        "id": {
                            "type": "integer",
                            "format": "int64",
                            "example": 100000
                        },
                        "username": {
                            "type": "string",
                            "example": "fehguy"
                        },
                        "address": {
                            "type": "array",
                            "xml": {
                                "name": "addresses",
                                "wrapped": true
                            },
                            "items": {
                                "$ref": "#/components/schemas/Address"
                            }
                        }
                    },
                    "xml": {
                        "name": "customer"
                    }
                },
                "Address": {
                    "type": "object",
                    "properties": {
                        "street": {
                            "type": "string",
                            "example": "437 Lytton"
                        },
                        "city": {
                            "type": "string",
                            "example": "Palo Alto"
                        },
                        "state": {
                            "type": "string",
                            "example": "CA"
                        },
                        "zip": {
                            "type": "string",
                            "example": "94301"
                        }
                    },
                    "xml": {
                        "name": "address"
                    }
                },
                "Category": {
                    "type": "object",
                    "properties": {
                        "id": {
                            "type": "integer",
                            "format": "int64",
                            "example": 1
                        },
                        "name": {
                            "type": "string",
                            "example": "Dogs"
                        }
                    },
                    "xml": {
                        "name": "category"
                    }
                },
                "User": {
                    "type": "object",
                    "properties": {
                        "id": {
                            "type": "integer",
                            "format": "int64",
                            "example": 10
                        },
                        "username": {
                            "type": "string",
                            "example": "theUser"
                        },
                        "firstName": {
                            "type": "string",
                            "example": "John"
                        },
                        "lastName": {
                            "type": "string",
                            "example": "James"
                        },
                        "email": {
                            "type": "string",
                            "example": "john@email.com"
                        },
                        "password": {
                            "type": "string",
                            "example": "12345"
                        },
                        "phone": {
                            "type": "string",
                            "example": "12345"
                        },
                        "userStatus": {
                            "type": "integer",
                            "description": "User Status",
                            "format": "int32",
                            "example": 1
                        }
                    },
                    "xml": {
                        "name": "user"
                    }
                },
                "Tag": {
                    "type": "object",
                    "properties": {
                        "id": {
                            "type": "integer",
                            "format": "int64"
                        },
                        "name": {
                            "type": "string"
                        }
                    },
                    "xml": {
                        "name": "tag"
                    }
                },
                "Pet": {
                    "required": [
                        "name",
                        "photoUrls"
                    ],
                    "type": "object",
                    "properties": {
                        "id": {
                            "type": "integer",
                            "format": "int64",
                            "example": 10
                        },
                        "name": {
                            "type": "string",
                            "example": "doggie"
                        },
                        "category": {
                            "$ref": "#/components/schemas/Category"
                        },
                        "photoUrls": {
                            "type": "array",
                            "xml": {
                                "wrapped": true
                            },
                            "items": {
                                "type": "string",
                                "xml": {
                                    "name": "photoUrl"
                                }
                            }
                        },
                        "tags": {
                            "type": "array",
                            "xml": {
                                "wrapped": true
                            },
                            "items": {
                                "$ref": "#/components/schemas/Tag"
                            }
                        },
                        "status": {
                            "type": "string",
                            "description": "pet status in the store",
                            "enum": [
                                "available",
                                "pending",
                                "sold"
                            ]
                        }
                    },
                    "xml": {
                        "name": "pet"
                    }
                },
                "ApiResponse": {
                    "type": "object",
                    "properties": {
                        "code": {
                            "type": "integer",
                            "format": "int32"
                        },
                        "type": {
                            "type": "string"
                        },
                        "message": {
                            "type": "string"
                        }
                    },
                    "xml": {
                        "name": "##default"
                    }
                }
            },
            "requestBodies": {
                "Pet": {
                    "description": "Pet object that needs to be added to the store",
                    "content": {
                        "application/json": {
                            "schema": {
                                "$ref": "#/components/schemas/Pet"
                            }
                        },
                        "application/xml": {
                            "schema": {
                                "$ref": "#/components/schemas/Pet"
                            }
                        }
                    }
                },
                "UserArray": {
                    "description": "List of user object",
                    "content": {
                        "application/json": {
                            "schema": {
                                "type": "array",
                                "items": {
                                    "$ref": "#/components/schemas/User"
                                }
                            }
                        }
                    }
                }
            },
            "securitySchemes": {
                "petstore_auth": {
                    "type": "oauth2",
                    "flows": {
                        "implicit": {
                            "authorizationUrl": "https://petstore3.swagger.io/oauth/authorize",
                            "scopes": {
                                "write:pets": "modify pets in your account",
                                "read:pets": "read your pets"
                            }
                        }
                    }
                },
                "api_key": {
                    "type": "apiKey",
                    "name": "api_key",
                    "in": "header"
                }
            }
        }
    }
    """;
}
